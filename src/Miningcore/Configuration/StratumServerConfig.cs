using System.ComponentModel.DataAnnotations;

namespace Miningcore.Configuration;

public class StratumServerConfig
{
    /// <summary>
    /// Server hostname or IP address
    /// </summary>
    [Required]
    public string Host { get; set; }

    /// <summary>
    /// Human-readable region name
    /// </summary>
    [Required] 
    public string Region { get; set; }

    /// <summary>
    /// Array of port numbers as strings
    /// </summary>
    [Required]
    public string[] Ports { get; set; }
}
