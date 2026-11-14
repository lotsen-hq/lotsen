<script lang="ts">
   const line = 'flex gap-2 px-2.5 whitespace-pre';
   const number = 'w-5 text-right text-muted-foreground';
</script>

<!-- Shows a few diff lines in the page's own palette, syntax colors and font size, so the reviewer sees the appearance settings together. -->
<figure class="overflow-hidden rounded-lg border bg-background" aria-label="Preview">
   <figcaption class="flex justify-between border-b bg-card px-2.5 py-1 text-xs text-muted-foreground">
      <span>placement.ts</span>
      <span>Preview</span>
   </figcaption>
   <div class="overflow-x-auto py-1 font-mono text-xs" aria-hidden="true">
      <!-- prettier-ignore -->
      <div class="{line} bg-diff-deleted-line"><span class={number}>12</span><span class="text-diff-deleted-marker">-</span><span><span class="text-syntax-keyword">const</span> len = stop.body.length;</span></div>
      <!-- prettier-ignore -->
      <div class="{line} bg-diff-inserted-line"><span class={number}>12</span><span class="text-diff-inserted-marker">+</span><span><span class="text-syntax-keyword">const</span> len: <span class="text-syntax-type">number</span> = <span class="text-syntax-function">bodyLength</span>(stop);</span></div>
      <!-- prettier-ignore -->
      <div class="{line} bg-diff-inserted-line"><span class={number}>13</span><span class="text-diff-inserted-marker">+</span><span><span class="text-syntax-keyword">if</span> (len &gt; <span class="text-syntax-number">600</span>) <span class="text-syntax-keyword">return</span> <span class="text-syntax-string">"side"</span>; <span class="syntax-comment">// long</span></span></div>
   </div>
</figure>
