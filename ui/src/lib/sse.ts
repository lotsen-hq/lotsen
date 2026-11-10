/** One event from a Server-Sent Events stream. */
export interface SseEvent {
   /** The event type, `message` when the stream named none. */
   type: string;
   /** The event's data lines, joined with line feeds. */
   data: string;
}

/**
 * Parses Server-Sent Events text as it arrives, in chunks that may split a line anywhere.
 *
 * It follows the HTML standard's event stream rules for comments, fields, `data` lines and the CR, LF and CRLF line endings. It ignores `id` and `retry`, because the event stream client reconnects on
 * its own schedule and the server does not replay events. See https://html.spec.whatwg.org/multipage/server-sent-events.html#event-stream-interpretation
 */
export class SseParser {
   private line = '';
   private skipLineFeed = false;
   private type = '';
   private dataLines: string[] = [];

   /** Parses the next chunk of text and returns the events that it completes. */
   push(chunk: string): SseEvent[] {
      const events: SseEvent[] = [];
      let start = 0;
      for (let i = 0; i < chunk.length; i++) {
         const char = chunk[i];
         if (char === '\n' && this.skipLineFeed) {
            // The LF of a CRLF whose CR ended the previous line, possibly in the previous chunk.
            this.skipLineFeed = false;
            start = i + 1;
            continue;
         }

         this.skipLineFeed = false;
         if (char === '\r' || char === '\n') {
            this.processLine(this.line + chunk.slice(start, i), events);
            this.line = '';
            this.skipLineFeed = char === '\r';
            start = i + 1;
         }
      }

      this.line += chunk.slice(start);
      return events;
   }

   private processLine(line: string, events: SseEvent[]): void {
      if (line === '') {
         if (this.dataLines.length > 0) {
            events.push({ type: this.type || 'message', data: this.dataLines.join('\n') });
         }

         this.type = '';
         this.dataLines = [];
         return;
      }

      if (line.startsWith(':')) {
         return;
      }

      const colon = line.indexOf(':');
      const field = colon < 0 ? line : line.slice(0, colon);
      let value = colon < 0 ? '' : line.slice(colon + 1);
      if (value.startsWith(' ')) {
         value = value.slice(1);
      }

      if (field === 'event') {
         this.type = value;
      } else if (field === 'data') {
         this.dataLines.push(value);
      }
   }
}

/**
 * Reads a Server-Sent Events body to its end and hands each event to `onEvent`.
 *
 * @param onChunk - Runs for each chunk that arrives, a comment included, before its events.
 * @returns A promise that resolves when the stream ends, and rejects when reading fails, `onEvent` throws or `signal` aborts.
 */
export async function readEventStream(body: ReadableStream<Uint8Array>, onEvent: (event: SseEvent) => void, signal?: AbortSignal, onChunk?: () => void): Promise<void> {
   const reader = body.getReader();
   const cancel = () => void reader.cancel().catch(() => {});
   signal?.addEventListener('abort', cancel, { once: true });
   try {
      // The decoder keeps a multi-byte character that a chunk boundary splits until its last byte arrives.
      const decoder = new TextDecoder();
      const parser = new SseParser();
      for (;;) {
         const { done, value } = await reader.read();
         if (done) {
            return;
         }

         onChunk?.();
         for (const event of parser.push(decoder.decode(value, { stream: true }))) {
            onEvent(event);
         }

         signal?.throwIfAborted();
      }
   } finally {
      signal?.removeEventListener('abort', cancel);
      reader.releaseLock();
   }
}
