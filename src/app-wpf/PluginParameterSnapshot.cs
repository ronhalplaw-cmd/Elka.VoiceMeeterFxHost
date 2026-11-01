using System.Globalization;

namespace Elka.VoiceMeeterFxHost.App;

// Reads the parameter snapshot the native layer saves next to a VST's state (capturePluginParameterStateBase64):
// "ELKA_PLUGIN_PARAMETERS_V1", the parameter count, then one "index<TAB>normalized value" line per parameter,
// stored in JUCE's MemoryBlock base64 form ("<byte count>.<6 bits per character, least significant bit first>").
internal static class PluginParameterSnapshot
{
    private const string Header = "ELKA_PLUGIN_PARAMETERS_V1";
    private const string JuceBase64Characters = ".ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+";

    // Normalized values read back after a restore can differ in the last float digits.
    private const float Tolerance = 1e-4f;

    public static bool Matches(string saved, string current)
    {
        if (string.Equals(saved, current, StringComparison.Ordinal))
        {
            return true;
        }

        var savedValues = Parse(saved);
        var currentValues = Parse(current);
        if (savedValues is null || currentValues is null || savedValues.Count != currentValues.Count)
        {
            return false;
        }

        foreach (var (index, value) in savedValues)
        {
            if (!currentValues.TryGetValue(index, out var other) || Math.Abs(value - other) > Tolerance)
            {
                return false;
            }
        }

        return true;
    }

    internal static Dictionary<int, float>? Parse(string encoded)
    {
        var text = DecodeJuceBase64(encoded);
        if (text is null)
        {
            return null;
        }

        var lines = text.Split('\n');
        if (lines.Length < 2 || lines[0] != Header ||
            !int.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            return null;
        }

        var values = new Dictionary<int, float>();
        foreach (var line in lines.Skip(2))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            values[index] = value;
        }

        return values.Count <= count ? values : null;
    }

    private static string? DecodeJuceBase64(string encoded)
    {
        var dot = encoded.IndexOf('.');
        if (dot <= 0 || !int.TryParse(encoded.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
            size < 0 || (long)(encoded.Length - dot - 1) * 6 < (long)size * 8)
        {
            return null;
        }

        var bytes = new byte[size];
        var bit = 0;
        for (var position = dot + 1; position < encoded.Length; ++position)
        {
            var sextet = JuceBase64Characters.IndexOf(encoded[position]);
            if (sextet < 0)
            {
                return null;
            }

            for (var sextetBit = 0; sextetBit < 6; ++sextetBit, ++bit)
            {
                if ((sextet & (1 << sextetBit)) != 0 && bit >> 3 < size)
                {
                    bytes[bit >> 3] |= (byte)(1 << (bit & 7));
                }
            }
        }

        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
