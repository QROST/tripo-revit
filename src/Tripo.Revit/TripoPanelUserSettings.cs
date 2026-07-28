using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tripo.Revit;

internal sealed record TripoPanelUserSettings(
    int FaceLimit = 20_000,
    bool WithMaterials = true,
    string ObjectName = "Tripo Model",
    string ImportMode = "family",
    bool ApplyMaterials = true,
    string PlacementMode = "origin",
    bool AssociateLevel = false)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string GetSettingsPath() =>
        Path.Combine(
            Tripo.Bridge.BridgePaths.GetRootDirectory(),
            "ui-settings",
            "revit-panel.json");

    public static TripoPanelUserSettings Load()
    {
        string path = GetSettingsPath();
        try
        {
            if (!File.Exists(path))
            {
                return new TripoPanelUserSettings();
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TripoPanelUserSettings>(
                       json,
                       JsonOptions) ??
                   new TripoPanelUserSettings();
        }
        catch
        {
            return new TripoPanelUserSettings();
        }
    }

    public void Save()
    {
        string path = GetSettingsPath();
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(this, JsonOptions));
    }
}
