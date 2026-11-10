using Lotsen.Tours;

namespace Lotsen.Prose;

/// <summary>Reads the code references of the <c>ref:</c> links in prose. Nothing here checks that the files or the lines exist at a commit.</summary>
internal static class ProseReferences
{
   /// <summary>Returns the code references in the prose fields of a stop's content, each under its field's <see cref="ProseField.Path"/>.</summary>
   /// <returns>The references or the fault, as <see cref="Read"/> returns them.</returns>
   public static Checked<IReadOnlyList<ProseReference>> Of(StopContent content) => Read(StopProse.Fields(content).Select(entry => (entry.Field.Path, entry.Prose)));

   /// <summary>Returns the code references in each field's prose, such as a stop's prose fields or the text of a reply.</summary>
   /// <param name="fields">Each field's path, such as <c>how</c> or <c>text</c>, with its prose.</param>
   /// <returns>
   /// The references in field order, each under its field, or the fault of the first field whose prose nests too deep or holds a malformed <c>ref:</c> link. The fault names the field and the link.
   /// </returns>
   public static Checked<IReadOnlyList<ProseReference>> Read(IEnumerable<(FieldPath Field, string Prose)> fields)
   {
      var references = new List<ProseReference>();
      foreach (var (field, prose) in fields)
      {
         RenderedProse rendered;
         try
         {
            rendered = ProseRenderer.Render(prose);
         }
         catch (ProseTooDeepException exception)
         {
            return new RuleFault(field, exception.Message);
         }

         if (rendered.MalformedReferences is [var malformed, ..])
         {
            return new RuleFault(field, $"the link '{malformed.Link}' is not a well-formed code reference. {malformed.Reason}");
         }

         references.AddRange(rendered.References.Select(reference => new ProseReference(field, reference)));
      }

      return references;
   }
}

/// <summary>A code reference in a prose field, such as a field of a stop's content or of <c>put_stop</c>'s or <c>reply</c>'s input.</summary>
/// <param name="Field">The field that holds the link, such as <c>how</c>, <c>checks[2].question</c> or <c>text</c>.</param>
/// <param name="Reference">The code reference that the link holds.</param>
internal sealed record ProseReference(FieldPath Field, CodeReference Reference);
