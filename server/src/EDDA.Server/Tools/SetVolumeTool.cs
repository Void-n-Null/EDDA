using System.ComponentModel;
using EDDA.Server.Services.Llm;
using EDDA.Server.Services.Session;
using Microsoft.Extensions.Logging;

namespace EDDA.Server.Tools;

/// <summary>
/// Tool for controlling the audio output volume on the Pi client.
/// </summary>
public sealed class SetVolumeTool : LlmTool<SetVolumeTool.Parameters>
{
    private readonly IMessageSinkAccessor _sinks;
    private readonly ILogger<SetVolumeTool>? _logger;

    public SetVolumeTool(IMessageSinkAccessor sinks, ILogger<SetVolumeTool>? logger = null)
    {
        _sinks = sinks;
        _logger = logger;
    }

    public override string Name => "set_volume";

    public override string Description =>
        "Control the audio output volume of your voice. " +
        "Use this when the user asks you to speak louder, quieter, turn up/down the volume, " +
        "or set the volume to a specific level. " +
        "Volume is 0-100 percent. Use 'change' for relative adjustments like 'turn it up a bit' (+10) or 'quieter' (-15).";

    public class Parameters
    {
        [Description("Absolute volume level (0-100). Use this when user says 'set volume to 50%' or 'volume at 30'.")]
        public int? Volume { get; set; }

        [Description("Relative volume change (-100 to +100). Use this for 'louder' (+15), 'quieter' (-15), 'much louder' (+30), etc.")]
        public int? Change { get; set; }
    }

    protected override async Task<ToolResult> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var sink = _sinks.Current;

        if (sink is null)
        {
            return ToolResult.Error("No active connection to the audio device.");
        }

        // Validate parameters
        if (parameters.Volume is null && parameters.Change is null)
        {
            return ToolResult.InvalidInput("Either 'volume' (0-100) or 'change' (+/-) must be specified.");
        }

        if (parameters.Volume is not null && parameters.Change is not null)
        {
            return ToolResult.InvalidInput("Specify either 'volume' or 'change', not both.");
        }

        try
        {
            if (parameters.Volume is not null)
            {
                // Absolute volume
                var vol = Math.Clamp(parameters.Volume.Value, 0, 100);
                await sink.SendVolumeAsync(vol, relative: false, ct);
                _logger?.LogInformation("Volume set to {Volume}%", vol);

                return ToolResult.Success(new
                {
                    action = "set",
                    volume = vol,
                    message = $"Volume set to {vol}%"
                });
            }
            else
            {
                // Relative change
                var change = Math.Clamp(parameters.Change!.Value, -100, 100);
                await sink.SendVolumeAsync(change, relative: true, ct);
                _logger?.LogInformation("Volume changed by {Change}%", change);

                var direction = change > 0 ? "increased" : "decreased";
                return ToolResult.Success(new
                {
                    action = "change",
                    change,
                    message = $"Volume {direction} by {Math.Abs(change)}%"
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to set volume");
            return ToolResult.Error($"Failed to adjust volume: {ex.Message}");
        }
    }
}
