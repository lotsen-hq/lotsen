<!-- The banner over a stop's explanation while the page shows an earlier stop version in place of the current one, with the way to the current version. -->
<script lang="ts">
   import { Button } from '$lib/components/ui/button';
   import { versionNoticeText } from '../lib/stopNotices';
   import type { ShownVersion } from '../lib/versions';

   let { shown, onshowcurrent }: { shown: ShownVersion; onshowcurrent: () => void } = $props();
</script>

<div
   role="status"
   aria-label="Stop version"
   class="mt-2 flex flex-wrap items-center gap-x-3 gap-y-1.5 rounded-md border border-l-4 border-l-primary bg-accent px-3 py-2 text-sm text-accent-foreground"
>
   <p class="min-w-48 flex-1">{versionNoticeText(shown)}</p>
   <Button variant="outline" size="sm" onclick={onshowcurrent}>{shown.reason.kind === 'opened' ? 'Show the current version' : 'Show the new version'}</Button>
</div>
