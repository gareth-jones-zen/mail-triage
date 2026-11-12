using System.Text;
using System.Text.Json.Nodes;
using MailTriage.Interim;

namespace MailTriage;

/// <summary>The JSON files: written exactly as the Python wrote them (json.dumps(indent=2, ensure_ascii=False) in
/// text mode, so CRLF line ends, no BOM), so either version can read the other's, and a rollback just works.</summary>
public static class StateFiles
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>triage._load: the parsed file, or the default if there's no file.</summary>
    public static JsonNode? Load(string path, Func<JsonNode?> missing)
    {
        string text;
        try
        {
            text = File.ReadAllText(path, new UTF8Encoding(false, true));
        }
        catch (FileNotFoundException)
        {
            return missing();
        }
        catch (DirectoryNotFoundException)
        {
            return missing();
        }
        return PyJson.Parse(text.Replace("\r\n", "\n").Replace('\r', '\n'));
    }

    public static JsonObject LoadObject(string path, Func<JsonObject> missing) =>
        Load(path, () => missing()) as JsonObject ?? throw new InvalidDataException($"{path} isn't a JSON object.");

    /// <summary>The text Python wrote for this data.</summary>
    public static string Text(JsonNode? data) => PyJson.Dump(data, 2, false).Replace("\n", "\r\n");

    /// <summary>triage._save: a temporary file beside it, then replaced in one step.</summary>
    public static void Save(string path, JsonNode? data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Text(data), Utf8);
        File.Move(temp, path, true);
    }
}
