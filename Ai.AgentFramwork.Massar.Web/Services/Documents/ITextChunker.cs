namespace Ai.AgentFramwork.Massar.Web.Services.Documents;

public interface ITextChunker
{
    List<string> Split(string text, int maxChars, int overlapChars);
}

public class SimpleTextChunker : ITextChunker
{
    public List<string> Split(string text, int maxChars, int overlapChars)
    {
        text = text ?? "";
        if (text.Length <= maxChars) return new() { text };

        var result = new List<string>();
        int start = 0;

        while (start < text.Length)
        {
            int len = Math.Min(maxChars, text.Length - start);
            var chunk = text.Substring(start, len);
            result.Add(chunk);

            start += (maxChars - overlapChars);
            if (start < 0) break;
        }

        return result;
    }
}