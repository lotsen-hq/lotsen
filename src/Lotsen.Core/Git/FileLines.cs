namespace Lotsen.Git;

/// <summary>
/// The lines of a file's text, as git counts them: a line ends at a line feed, and text after the last line feed is one more line.
/// </summary>
/// <remarks>
/// A carriage return before a line feed belongs to its line, so a CRLF file has as many lines as the same file with LF. The offsets count UTF-16 code units, as JavaScript strings do.
/// </remarks>
internal sealed class FileLines
{
   private const char ByteOrderMark = '﻿';

   private readonly string _text;
   private readonly List<int> _starts = [];

   public FileLines(string text)
   {
      _text = text;
      if (text.Length > 0)
      {
         _starts.Add(0);
      }

      for (var i = text.IndexOf('\n'); i >= 0 && i + 1 < text.Length; i = text.IndexOf('\n', i + 1))
      {
         _starts.Add(i + 1);
      }
   }

   /// <summary>The number of lines, 0 for an empty text.</summary>
   public int Count => _starts.Count;

   /// <summary>The length of the text in UTF-16 code units.</summary>
   public int Length => _text.Length;

   /// <summary>Returns the offset where a line starts.</summary>
   /// <param name="line">A line from 1 to <see cref="Count"/>.</param>
   public int StartOf(int line) => _starts[line - 1];

   /// <summary>Returns the offset after a line's line break, or the text's length for a last line without one.</summary>
   /// <param name="line">A line from 1 to <see cref="Count"/>.</param>
   public int EndOf(int line) => line < Count ? _starts[line] : _text.Length;

   /// <summary>Returns the offsets that one side of a <see cref="LineHunk"/> covers: whole lines with their line breaks.</summary>
   /// <param name="start">The side's start line from the hunk.</param>
   /// <param name="count">The side's line count from the hunk. With a count of 0 the range is the insertion point after line <paramref name="start"/>, so both ends are equal.</param>
   public (int From, int To) HunkRange(int start, int count)
   {
      if (count == 0)
      {
         var point = StartOrEnd(start + 1);
         return (point, point);
      }

      return (StartOrEnd(start), StartOrEnd(start + count));
   }

   /// <summary>Returns the text of one line, as <see cref="Text"/> returns it.</summary>
   public string Line(int line) => Text(line, line);

   /// <summary>Returns the text of the lines from <paramref name="startLine"/> to <paramref name="endLine"/>.</summary>
   /// <remarks>The text keeps the line breaks between the lines as the file has them. It leaves out the line break after the last line and a UTF-8 byte order mark at the start of the file.</remarks>
   public string Text(int startLine, int endLine)
   {
      var (from, to) = (StartOf(startLine), EndOf(endLine));
      if (to > from && _text[to - 1] == '\n')
      {
         to -= to - 1 > from && _text[to - 2] == '\r' ? 2 : 1;
      }

      if (from == 0 && to > 0 && _text[0] == ByteOrderMark)
      {
         from = 1;
      }

      return _text[from..to];
   }

   // A line past the last one starts at the end of the text.
   private int StartOrEnd(int line) => line <= Count ? StartOf(line) : _text.Length;
}
