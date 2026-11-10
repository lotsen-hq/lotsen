import { describe, expect, it } from 'vitest';
import { readEventStream, SseParser, type SseEvent } from '../src/lib/sse';

function parse(...chunks: string[]): SseEvent[] {
   const parser = new SseParser();
   return chunks.flatMap((chunk) => parser.push(chunk));
}

describe('SseParser', () => {
   it('reads the event type and data of each event', () => {
      expect(parse('event: tourCreated\ndata: {"id":"a"}\n\nevent: planSet\ndata: {}\n\n')).toEqual([
         { type: 'tourCreated', data: '{"id":"a"}' },
         { type: 'planSet', data: '{}' },
      ]);
   });

   it('ignores comments, such as the keep-alive lines', () => {
      expect(parse(': keep-alive\n\n:no space\n\nevent: planSet\n: inside\ndata: x\n\n')).toEqual([{ type: 'planSet', data: 'x' }]);
   });

   it('joins multi-line data with line feeds', () => {
      expect(parse('data: first\ndata: second\ndata:\n\n')).toEqual([{ type: 'message', data: 'first\nsecond\n' }]);
   });

   it('removes one space after the colon and keeps the rest', () => {
      expect(parse('data:no space\n\ndata:  two spaces\n\n')).toEqual([
         { type: 'message', data: 'no space' },
         { type: 'message', data: ' two spaces' },
      ]);
   });

   it('treats a line without a colon as a field with an empty value', () => {
      expect(parse('data\n\n')).toEqual([{ type: 'message', data: '' }]);
   });

   it('dispatches nothing for a block without data, and resets the type after each event', () => {
      expect(parse('event: lonely\n\ndata: plain\n\n')).toEqual([{ type: 'message', data: 'plain' }]);
   });

   it('ignores id, retry and unknown fields', () => {
      expect(parse('id: 7\nretry: 10\nfoo: bar\ndata: x\n\n')).toEqual([{ type: 'message', data: 'x' }]);
   });

   it('accepts CRLF, CR and LF line endings', () => {
      expect(parse('event: a\r\ndata: 1\r\n\r\nevent: b\rdata: 2\r\revent: c\ndata: 3\n\n')).toEqual([
         { type: 'a', data: '1' },
         { type: 'b', data: '2' },
         { type: 'c', data: '3' },
      ]);
   });

   it('joins lines that chunk boundaries split anywhere', () => {
      const text = 'event: planSet\r\ndata: {"a":1}\r\ndata: {"b":2}\r\n\r\n: keep-alive\n\nevent: x\ndata: y\n\n';
      const expected = [
         { type: 'planSet', data: '{"a":1}\n{"b":2}' },
         { type: 'x', data: 'y' },
      ];
      for (let size = 1; size <= text.length; size++) {
         const chunks = [];
         for (let start = 0; start < text.length; start += size) {
            chunks.push(text.slice(start, start + size));
         }

         expect(parse(...chunks), `chunks of ${size}`).toEqual(expected);
      }
   });

   it('does not take a CRLF split across chunks for two line endings', () => {
      expect(parse('data: a\r', '\ndata: b\r', '\n\r', '\n')).toEqual([{ type: 'message', data: 'a\nb' }]);
   });

   it('holds an event back until its blank line arrives', () => {
      const parser = new SseParser();
      expect(parser.push('event: planSet\ndata: x\n')).toEqual([]);
      expect(parser.push('\n')).toEqual([{ type: 'planSet', data: 'x' }]);
   });
});

describe('readEventStream', () => {
   function streamOf(chunks: Uint8Array[]): ReadableStream<Uint8Array> {
      return new ReadableStream({
         start(controller) {
            for (const chunk of chunks) {
               controller.enqueue(chunk);
            }

            controller.close();
         },
      });
   }

   it('decodes UTF-8 characters that a chunk boundary splits', async () => {
      const bytes = new TextEncoder().encode('data: Grüße ✓\n\n');
      const split = bytes.indexOf(0xc3) + 1;
      const events: SseEvent[] = [];

      await readEventStream(streamOf([bytes.slice(0, split), bytes.slice(split)]), (event) => events.push(event));

      expect(events).toEqual([{ type: 'message', data: 'Grüße ✓' }]);
   });

   it('drops an event that the stream ends before its blank line', async () => {
      const events: SseEvent[] = [];

      await readEventStream(streamOf([new TextEncoder().encode('data: cut off\n')]), (event) => events.push(event));

      expect(events).toEqual([]);
   });
});
