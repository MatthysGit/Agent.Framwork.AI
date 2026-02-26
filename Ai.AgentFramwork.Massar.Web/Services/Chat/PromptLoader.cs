namespace Ai.AgentFramwork.Massar.Web.Services.Chat;

internal static class PromptLoader
{
    public static string LoadEmbeddedBySuffix(Type anchorType, string suffix)
    {
        var asm = anchorType.Assembly;
        var match = asm.GetManifestResourceNames()
            .SingleOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));

        if (match is null)
            throw new InvalidOperationException(
                $"Missing embedded prompt suffix '{suffix}'. Available:\n" +
                string.Join("\n", asm.GetManifestResourceNames()));

        using var stream = asm.GetManifestResourceStream(match)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
