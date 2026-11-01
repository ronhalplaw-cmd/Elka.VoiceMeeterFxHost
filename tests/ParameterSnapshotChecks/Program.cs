using System.Globalization;
using System.Text;
using Elka.VoiceMeeterFxHost.App;

internal static class Program
{
    private static int _checks;

    private static void Main()
    {
        var saved = Snapshot(0.141438067f, 0.25f, 0.5f, 1f, 0f);
        Check(PluginParameterSnapshot.Parse(saved)?.Count == 5, "a snapshot in JUCE base64 form is read");
        Check(PluginParameterSnapshot.Matches(saved, saved), "identical snapshots match");
        Check(PluginParameterSnapshot.Matches(saved, Snapshot(0.14144f, 0.25f, 0.5f, 1f, 0f)),
            "float read-back noise below the tolerance still matches");
        Check(!PluginParameterSnapshot.Matches(saved, Snapshot(0.141438067f, 0.75f, 0.5f, 1f, 0f)),
            "a changed parameter does not match");
        Check(!PluginParameterSnapshot.Matches(saved, Snapshot(0.141438067f, 0.25f, 0.5f, 1f)),
            "a different parameter count does not match");
        Check(!PluginParameterSnapshot.Matches(saved, ""), "an empty read-back does not match");
        Check(!PluginParameterSnapshot.Matches(saved, "12.notjuce!"), "an unreadable read-back does not match");
        Check(PluginParameterSnapshot.Parse(Encode("SOMETHING_ELSE\n1\n0\t0.5\n")) is null, "an unknown header is rejected");
        Check(PluginParameterSnapshot.Matches(Snapshot(), Snapshot()), "plugins without parameters match");
        Console.WriteLine($"PASS: {_checks} parameter snapshot checks. No audio engine started.");
    }

    // Same text as capturePluginParameterStateBase64 writes.
    private static string Snapshot(params float[] values)
    {
        var text = new StringBuilder("ELKA_PLUGIN_PARAMETERS_V1\n");
        text.Append(values.Length).Append('\n');
        for (var index = 0; index < values.Length; ++index)
        {
            text.Append(index).Append('\t').Append(values[index].ToString("G9", CultureInfo.InvariantCulture)).Append('\n');
        }

        return Encode(text.ToString());
    }

    // juce::MemoryBlock::toBase64Encoding: "<byte count>." then 6 bits per character, least significant bit first.
    private static string Encode(string text)
    {
        const string characters = ".ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+";
        var bytes = Encoding.UTF8.GetBytes(text);
        var output = new StringBuilder().Append(bytes.Length).Append('.');
        var characterCount = (bytes.Length * 8 + 5) / 6;
        for (var index = 0; index < characterCount; ++index)
        {
            var sextet = 0;
            for (var bit = 0; bit < 6; ++bit)
            {
                var position = index * 6 + bit;
                if (position >> 3 < bytes.Length && (bytes[position >> 3] & (1 << (position & 7))) != 0)
                {
                    sextet |= 1 << bit;
                }
            }

            output.Append(characters[sextet]);
        }

        return output.ToString();
    }

    private static void Check(bool pass, string name)
    {
        if (!pass) throw new InvalidOperationException(name);
        _checks++;
    }
}
