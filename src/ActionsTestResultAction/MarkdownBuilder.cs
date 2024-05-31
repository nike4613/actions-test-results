using System.Text;

namespace ActionsTestResultAction
{
    internal sealed class MarkdownBuilder
    {
        private readonly StringBuilder sb;
        private readonly string indentString;

        public MarkdownBuilder(StringBuilder sb, string indentString = "> ")
        {
            this.sb = sb;
            this.indentString = indentString;
        }

        private int indentLevel;
        private bool didWriteIndent;

        public MarkdownBuilder IncreaseIndent()
        {
            indentLevel++;
            return this;
        }

        public MarkdownBuilder DecreaseIndent()
        {
            indentLevel--;
            return this;
        }

        private void WriteIndentIfNeeded()
        {
            if (didWriteIndent)
            {
                return;
            }

            if (indentLevel > 0)
            {
                for (var i = 0; i < indentLevel; i++)
                {
                    _ = sb.Append(indentString);
                }
            }
            didWriteIndent = true;
        }

        public MarkdownBuilder Append(string s)
        {
            WriteIndentIfNeeded();

            if (s.Contains('\n'))
            {
                var lines = s.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].TrimEnd('\r');
                    _ = sb.Append(line);
                    if (i != lines.Length - 1)
                    {
                        _ = AppendLine();
                        WriteIndentIfNeeded();
                    }
                }
            }
            else
            {
                _ = sb.Append(s);
            }
            return this;
        }

        public MarkdownBuilder Append(char c)
        {
            WriteIndentIfNeeded();
            _ = sb.Append(c);
            return this;
        }

        public MarkdownBuilder AppendLine(string s)
        {
            _ = Append(s);
            _ = sb.AppendLine();
            didWriteIndent = false;
            return this;
        }

        public MarkdownBuilder AppendLine(char c)
        {
            _ = Append(c);
            _ = sb.AppendLine();
            didWriteIndent = false;
            return this;
        }

        public MarkdownBuilder AppendLine()
        {
            WriteIndentIfNeeded();
            _ = sb.AppendLine();
            didWriteIndent = false;
            return this;
        }
    }
}
