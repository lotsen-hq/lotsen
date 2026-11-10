using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The limits of the reviewer's words: a question's, a note's, an answer's, a tour action's or an approval's.</summary>
internal static class ReviewerWords
{
   /// <summary>The most characters that the reviewer's words may hold.</summary>
   public const int MaxLength = 10_000;

   /// <summary>Returns why the words are missing or break a limit, or null when they keep the limits.</summary>
   /// <param name="text">The words.</param>
   /// <param name="noun">The words' noun, such as <c>question</c> or <c>answer</c>, which the refusal names them with.</param>
   public static OperationError? Check(string text, string noun = "question")
   {
      if (string.IsNullOrWhiteSpace(text))
      {
         return OperationError.Invalid("text", $"{Article(noun)} {noun} needs the reviewer's words.");
      }

      return Limit(text, noun) is { } limit ? OperationError.Invalid("text", limit) : null;
   }

   /// <summary>Returns why the text breaks a limit of the reviewer's words: its length or a control character.</summary>
   /// <remarks><c>put_stop</c> holds each suggestion to these limits too, because a pick stores the suggestion's text as the reviewer's answer.</remarks>
   /// <param name="noun">The words' noun, such as <c>answer</c> or <c>suggestion</c>, which the reason names them with.</param>
   /// <returns>The reason, or null when the text keeps the limits.</returns>
   public static string? Limit(string text, string noun)
   {
      if (text.Length > MaxLength)
      {
         return $"{Article(noun)} {noun} holds at most {MaxLength} characters, and this one holds {text.Length}. Shorten it.";
      }

      return text.Any(character => char.IsControl(character) && character is not ('\t' or '\n' or '\r'))
         ? $"the {noun} holds a control character. Only tabs and line breaks may appear in it."
         : null;
   }

   private static string Article(string noun) => noun.StartsWith('a') ? "an" : "a";
}
