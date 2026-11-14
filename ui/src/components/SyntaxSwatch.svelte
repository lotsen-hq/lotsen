<script lang="ts">
   import type { SyntaxColors } from '../lib/syntaxColors';

   let { family, kind }: { family: SyntaxColors; kind: 'dots' | 'code' } = $props();
</script>

<!-- data-syntax-swatch gives the element the family's palette whatever family the page shows (syntax-colors.css). -->
{#if kind === 'dots'}
   <span data-syntax-swatch={family} aria-hidden="true" class="flex shrink-0 gap-0.75">
      <span class="size-2 rounded-full bg-syntax-keyword"></span>
      <span class="size-2 rounded-full bg-syntax-string"></span>
      <span class="size-2 rounded-full bg-syntax-number"></span>
      <span class="size-2 rounded-full bg-syntax-type"></span>
      <span class="size-2 rounded-full bg-syntax-function"></span>
   </span>
{:else}
   <!-- prettier-ignore -->
   <code data-syntax-swatch={family} aria-hidden="true" class="font-mono text-xs whitespace-pre"><span class="text-syntax-keyword">let</span> s: <span class="text-syntax-type">Stop</span> = <span class="text-syntax-function">find</span>(<span class="text-syntax-string">"id"</span>, <span class="text-syntax-number">42</span>); <span class="syntax-comment">// ok</span></code>
{/if}
