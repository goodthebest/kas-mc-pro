use std::collections::HashMap;
use std::str::FromStr;
use std::sync::Arc;
use std::time::Duration;

use kaspa_addresses::{Address, AddressError, Prefix, Version};
use kaspa_bip32::{
    DerivationPath, ExtendedPrivateKey, Language, Mnemonic, Prefix as KeyPrefix, SecretKey,
    SecretKeyExt,
};
use kaspa_consensus_core::sign::{sign_with_multiple_v2, Signed};
use kaspa_consensus_core::tx::{SignableTransaction, Transaction, TransactionOutpoint, UtxoEntry};
use kaspa_grpc_client::GrpcClient;
use kaspa_rpc_core::api::rpc::RpcApi;
use kaspa_rpc_core::error::RpcError;
use kaspa_rpc_core::model::address::RpcUtxosByAddressesEntry;
use kaspa_rpc_core::model::message::GetServerInfoResponse;
use kaspa_rpc_core::model::tx::{RpcTransaction, RpcTransactionId, RpcTransactionOutpoint};
use kaspa_wrpc_client::client::{ConnectOptions, ConnectStrategy};
use kaspa_wrpc_client::prelude::{NetworkId, NetworkType, WrpcEncoding};
use kaspa_wrpc_client::KaspaRpcClient;
use thiserror::Error;
use tokio::runtime::Runtime;

#[derive(Clone, Debug)]
pub struct WalletConfig {
    pub network_type: NetworkType,
    pub wrpc_url: Option<String>,
    pub wrpc_encoding: WrpcEncoding,
    pub wrpc_connect_timeout: Option<Duration>,
    pub grpc_url: Option<String>,
}

impl WalletConfig {
    pub fn new(network_type: NetworkType) -> Self {
        Self {
            network_type,
            wrpc_url: None,
            wrpc_encoding: WrpcEncoding::Borsh,
            wrpc_connect_timeout: Some(Duration::from_secs(10)),
            grpc_url: None,
        }
    }

    pub fn with_wrpc_url(mut self, url: impl Into<String>) -> Self {
        self.wrpc_url = Some(url.into());
        self
    }

    pub fn with_grpc_url(mut self, url: impl Into<String>) -> Self {
        self.grpc_url = Some(url.into());
        self
    }
}

#[derive(Debug, Error)]
pub enum WalletError {
    #[error("no Kaspa RPC endpoints configured")]
    NoEndpoints,
    #[error("Kaspa wRPC error: {0}")]
    Wrpc(#[from] kaspa_wrpc_client::error::Error),
    #[error("Kaspa gRPC error: {0}")]
    Grpc(#[from] kaspa_grpc_client::error::Error),
    #[error("Kaspa RPC error: {0}")]
    Rpc(#[from] RpcError),
    #[error("invalid address: {0}")]
    Address(#[from] AddressError),
    #[error("BIP32 error: {0}")]
    Bip32(#[from] kaspa_bip32::Error),
    #[error("hex decoding error: {0}")]
    Hex(#[from] hex::FromHexError),
    #[error("missing UTXO entry for outpoint {0:?}")]
    MissingUtxo(TransactionOutpoint),
    #[error("transaction is only partially signed")]
    PartialSignature,
    #[error("kaspa node does not expose the UTXO index; restart kaspad with --utxoindex")]
    MissingUtxoIndex,
}

#[derive(Clone, Debug)]
pub struct DerivedKey {
    pub extended_private_key: String,
    pub private_key_hex: String,
    pub public_key_hex: String,
    pub x_only_public_key_hex: String,
    pub address: String,
}

pub struct RustyKaspaWallet {
    runtime: Runtime,
    network_type: NetworkType,
    wrpc_client: Option<Arc<KaspaRpcClient>>,
    grpc_client: Option<Arc<GrpcClient>>,
}

impl RustyKaspaWallet {
    pub fn connect(config: WalletConfig) -> Result<Self, WalletError> {
        let runtime = Runtime::new().expect("failed to start tokio runtime");
        let mut wrpc_client = None;
        let mut grpc_client = None;
        let network_id = NetworkId::new(config.network_type);

        if let Some(url) = &config.wrpc_url {
            let client = KaspaRpcClient::new(
                config.wrpc_encoding,
                Some(url.as_str()),
                None,
                Some(network_id),
                None,
            )?;
            let connect_opts = ConnectOptions {
                block_async_connect: true,
                connect_timeout: config.wrpc_connect_timeout,
                strategy: ConnectStrategy::Fallback,
                ..Default::default()
            };
            runtime.block_on(client.connect(Some(connect_opts)))?;
            runtime.block_on(client.start())?;
            ensure_utxo_index(&runtime, &client)?;
            wrpc_client = Some(Arc::new(client));
        }

        if let Some(url) = &config.grpc_url {
            let grpc_url = if url.starts_with("grpc://") {
                url.clone()
            } else {
                format!("grpc://{url}")
            };
            let client = runtime.block_on(GrpcClient::connect(grpc_url.clone()))?;
            runtime.block_on(client.start(None));
            ensure_utxo_index_grpc(&runtime, &client)?;
            grpc_client = Some(Arc::new(client));
        }

        if wrpc_client.is_none() && grpc_client.is_none() {
            return Err(WalletError::NoEndpoints);
        }

        Ok(Self {
            runtime,
            network_type: config.network_type,
            wrpc_client,
            grpc_client,
        })
    }

    pub fn get_utxos(&self, address: &str) -> Result<Vec<RpcUtxosByAddressesEntry>, WalletError> {
        let rpc_address: Address = address.try_into()?;

        if let Some(client) = &self.wrpc_client {
            return self
                .runtime
                .block_on(client.get_utxos_by_addresses(vec![rpc_address.clone()]))
                .map_err(WalletError::from);
        }

        if let Some(client) = &self.grpc_client {
            return self
                .runtime
                .block_on(client.get_utxos_by_addresses(vec![rpc_address]))
                .map_err(WalletError::from);
        }

        Err(WalletError::NoEndpoints)
    }

    pub fn broadcast_transaction(
        &self,
        transaction: &Transaction,
        allow_orphans: bool,
    ) -> Result<RpcTransactionId, WalletError> {
        let rpc_tx = RpcTransaction::from(transaction);

        if let Some(client) = &self.wrpc_client {
            return self
                .runtime
                .block_on(client.submit_transaction(rpc_tx.clone(), allow_orphans))
                .map_err(WalletError::from);
        }

        if let Some(client) = &self.grpc_client {
            return self
                .runtime
                .block_on(client.submit_transaction(rpc_tx, allow_orphans))
                .map_err(WalletError::from);
        }

        Err(WalletError::NoEndpoints)
    }

    pub fn sign_transaction(
        &self,
        transaction: Transaction,
        utxos: &[RpcUtxosByAddressesEntry],
        private_keys: &[String],
    ) -> Result<Transaction, WalletError> {
        let mut utxo_map: HashMap<RpcTransactionOutpoint, UtxoEntry> = HashMap::new();
        for entry in utxos.iter() {
            let utxo = entry.utxo_entry.clone().into();
            utxo_map.insert(entry.outpoint, utxo);
        }

        let mut entries = Vec::with_capacity(transaction.inputs.len());
        for input in &transaction.inputs {
            let rpc_outpoint: RpcTransactionOutpoint = input.previous_outpoint.into();
            let utxo = utxo_map
                .get(&rpc_outpoint)
                .cloned()
                .ok_or_else(|| WalletError::MissingUtxo(input.previous_outpoint))?;
            entries.push(utxo);
        }

        let signable = SignableTransaction::with_entries(transaction, entries);
        let mut key_bytes = Vec::with_capacity(private_keys.len());
        for key in private_keys {
            let data = hex::decode(key)?;
            let array: [u8; 32] = data
                .as_slice()
                .try_into()
                .map_err(|_| WalletError::Hex(hex::FromHexError::InvalidStringLength))?;
            key_bytes.push(array);
        }

        let signed = sign_with_multiple_v2(signable, &key_bytes);
        let completed = match signed {
            Signed::Fully(tx) => tx,
            Signed::Partially(_) => return Err(WalletError::PartialSignature),
        };
        let mut tx = completed.tx;
        tx.finalize();
        Ok(tx)
    }

    pub fn derive_private_key(
        &self,
        mnemonic: &str,
        path: &str,
    ) -> Result<DerivedKey, WalletError> {
        let mnemonic = Mnemonic::new(mnemonic, Language::English)?;
        let seed = mnemonic.to_seed("");
        let master = ExtendedPrivateKey::<SecretKey>::new(seed.as_bytes())?;
        let derivation_path = DerivationPath::from_str(path)?;
        let child = master.derive_path(&derivation_path)?;
        let secret_key = child.private_key();
        let public_key = secret_key.get_public_key();
        let (x_only, _) = public_key.x_only_public_key();
        let prefix = Prefix::from(self.network_type);
        let address = Address::new(prefix, Version::PubKey, &x_only.serialize());

        let extended = child.to_string(KeyPrefix::KPRV);
        let private_key_hex = hex::encode(secret_key.secret_bytes());
        let public_key_hex = hex::encode(public_key.serialize());
        let x_only_public_key_hex = hex::encode(x_only.serialize());

        Ok(DerivedKey {
            extended_private_key: extended.to_string(),
            private_key_hex,
            public_key_hex,
            x_only_public_key_hex,
            address: address.to_string(),
        })
    }
}

impl Drop for RustyKaspaWallet {
    fn drop(&mut self) {
        if let Some(client) = self.wrpc_client.take() {
            let _ = self.runtime.block_on(async {
                client.stop().await.ok();
                client.disconnect().await.ok();
            });
        }
        if let Some(client) = self.grpc_client.take() {
            let _ = self.runtime.block_on(async {
                client.join().await.ok();
                client.disconnect().await.ok();
            });
        }
    }
}

fn ensure_utxo_index(runtime: &Runtime, client: &KaspaRpcClient) -> Result<(), WalletError> {
    let response = runtime
        .block_on(client.get_server_info())
        .map_err(WalletError::from)?;
    enforce_utxo_index(response)
}

fn ensure_utxo_index_grpc(runtime: &Runtime, client: &GrpcClient) -> Result<(), WalletError> {
    let response = runtime
        .block_on(client.get_server_info())
        .map_err(WalletError::from)?;
    enforce_utxo_index(response)
}

fn enforce_utxo_index(info: GetServerInfoResponse) -> Result<(), WalletError> {
    if !info.has_utxo_index {
        return Err(WalletError::MissingUtxoIndex);
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn derive_known_key() {
        let wallet = RustyKaspaWallet {
            runtime: Runtime::new().unwrap(),
            network_type: NetworkType::Mainnet,
            wrpc_client: None,
            grpc_client: None,
        };
        let mnemonic =
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        let derived = wallet
            .derive_private_key(mnemonic, "m/44'/972/0'/0/0")
            .expect("derive");
        assert_eq!(
            derived.extended_private_key,
            "kprv69KonnpFRxMFJg92dsShntS8TUDANxfEMqSdqv8U9qmGFdfAXfTErjXwLo3qUCgNQWyNnLp5CErPZJ5Y4JEacUS4ExLRMYftQH3FBXyUdH5"
        );
        assert_eq!(
            derived.private_key_hex,
            "53397ef426ef62f497eac3915fb2465edd9077650e67a50367d9f08ee7b9c0d1"
        );
        assert_eq!(
            derived.x_only_public_key_hex,
            "a27527b7c1c2c7867134f956bf2bb3611f6abbc78413b870186225ae870f34cc"
        );
        assert_eq!(
            derived.address,
            "kaspa:qz382fahc8pv0pn3xnu4d0etkds3764mc7zp8wrsrp3ztt58pu6vclrs67rdl"
        );
    }
}
