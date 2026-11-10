// The demo's sample tours, as seed specs that demo/seed.ts takes through the server's real operations. The content is invented.
import type { AnswerType, Familiarity, FileMarkKind, FileSide, PullRequestState, StopKind, StopPlacement } from '../src/lib/contract.generated.ts';

/** A passage as `[side, path, startLine, endLine, label]`, with 1-based lines. The first passage of a stop's content is its primary passage. */
export type SamplePassage = [side: FileSide, path: string, startLine: number, endLine: number, label?: string];

/** A check of sample content, named by a key unique within its tour, which the seed maps to the ID that the server gives the check. */
export interface SampleCheck {
   key: string;
   question: string;
   recommendation?: string;
   suggestions?: { text: string; type: AnswerType }[];
}

/** The content of a `put_stop`, in Markdown, with passages on real lines of the sample repository. */
export interface SampleContent {
   headline: string;
   how?: string;
   why?: string;
   designChoices?: string;
   pinnedBy?: string;
   scenario?: string;
   tags?: string[];
   placement?: StopPlacement;
   /** A new title for the stop, which a revision may give. */
   title?: string;
   passages?: SamplePassage[];
   checks?: SampleCheck[];
}

/** A stop of a sample plan, named by a key unique within its tour. */
export interface SampleStop {
   key: string;
   title: string;
   kind: StopKind;
   anchorHint?: { path: string; line?: number };
   /** The content that the seed writes, or none for a stop that stays pending. */
   content?: SampleContent;
   /** The content that the demo panel's button writes while the stop is pending. */
   later?: SampleContent;
}

/**
 * A place in a tour, by the keys of its stops and checks: a stop text anchor names its field as the server does, with a check's key in place of its ID, such as
 * `checks[fixed-message].recommendation`.
 */
export type SampleAnchor =
   | { kind: 'code'; side: FileSide; path: string; startLine: number; endLine: number }
   | { kind: 'file'; side: FileSide; path: string }
   | { kind: 'stopText'; stop: string; field: string; text: string }
   | { kind: 'stop'; stop: string }
   | { kind: 'tour' };

/** A tour action that the reviewer requests, by the keys of its stops. */
export type SampleAction = { kind: 'explain' | 'revise'; stop: string } | { kind: 'replan' } | { kind: 'reorder'; stops: string[] };

/**
 * One step of a tour's seed, which the reviewer, Claude or the platform takes through a real operation, in order. A thread is named by a key, which the steps after its start use.
 * The reviewer's steps are ask, followUp, resend, cancel, resolve, action, familiarity, note, answer, sendAnswers, skip, progress, archive and markReviewed. Claude's steps, as the bound
 * session, are reply, startThread, addMessage, outdate, write, fetchQuestion, fetchAnswers and synthesis. A newSession step ends the bound session and attaches the tour to a new one.
 */
export type SampleStep =
   | { ask: string; anchor: SampleAnchor; text: string; stop?: string }
   | { reply: string; resolution: 'done' | 'needsAnswer' | 'declined'; text?: string }
   | { followUp: string; text: string }
   | { resend: string }
   | { cancel: string }
   | { resolve: string }
   | { startThread: string; anchor: SampleAnchor; text: string }
   | { addMessage: string; text: string }
   | { outdate: string }
   | { action: string; request: SampleAction; text?: string }
   | { familiarity: string; level: Familiarity }
   | { write: string; content: SampleContent }
   | { note: string; anchor: SampleAnchor; text: string; stop?: string }
   | { answer: [stop: string, check: string]; text: string; type: AnswerType }
   | { sendAnswers: true }
   | { fetchAnswers: true }
   | { fetchQuestion: string }
   | { synthesis: string }
   | { newSession: true }
   | { skip: string }
   | { progress: string | 'summary' }
   | { archive: true }
   | { markReviewed: string; atFirstHead?: true };

/**
 * A sample tour of pull request `number` of the sample repository, created `ageMinutes` before the template's build.
 *
 * Its pin is `pinned` on the pushed head, `firstHead` on the first head with the pushed one as its available iteration, `failed` with the platform's reason, or `pinning` while the platform holds
 * its answer, which no template keeps, because the pin finishes once the platform answers.
 */
export interface SampleTour {
   key: string;
   number: number;
   ageMinutes: number;
   familiarity: Familiarity;
   planFirst?: true;
   pin: 'pinned' | 'firstHead' | 'pinning' | { failed: string };
   /** The pull request's state that an iteration check learns once the tour is pinned, or none for an active pull request. */
   pullRequestState?: Exclude<PullRequestState, 'active'>;
   chapters?: { title: string; stops: SampleStop[] }[];
   fileMarks?: { path: string; mark: FileMarkKind; reason: string }[];
   /** The attachment level of the session that each world binds to the tour, or none for a tour without a session. */
   session?: 'confirmed' | 'unconfirmed';
   /** The steps that the template takes after the plan and the stops' first content. */
   steps?: SampleStep[];
   /** The steps that each world takes once its sessions are bound, for what no template can keep: a request that waits, which a server's start leaves unanswered. */
   live?: SampleStep[];
}

/** The host of the pull request URLs that every world's platform recognizes, with the sample repository's name. */
export const sampleRepositoryUrl = 'https://platform.test/storefront';

// A JSON array of `count` invented products, one per line.
function products(count: number, prefix: string): string {
   const lines = Array.from({ length: count }, (_, index) => {
      const id = `${prefix}-${String(index + 1).padStart(5, '0')}`;
      return `  { "id": "${id}", "name": "Product ${index + 1}", "price": ${(1 + ((index * 37) % 9000) / 100).toFixed(2)}, "stock": ${(index * 13) % 250} }`;
   });
   return `[\n${lines.join(',\n')}\n]\n`;
}

// The shipping rates of 130 regions, one per line, without the regions from the first to the last of `dropped`.
function shippingRates(dropped: [first: number, last: number] | null): string {
   const regions = Array.from({ length: 130 }, (_, index) => index + 1).filter((region) => dropped === null || region < dropped[0] || region > dropped[1]);
   const lines = regions.map((region) => `  'region-${String(region).padStart(3, '0')}': ${100 + ((region * 17) % 400)},`);
   return `// The shipping rate of each region, in cents per kilogram.\nexport const rates: Record<string, number> = {\n${lines.join('\n')}\n};\n`;
}

// Git treats a blob with a NUL byte in its first 8000 bytes as binary.
const binary = (name: string) => `\u0089PNG\r\n\u001a\n\u0000\u0000\u0000\rIHDR ${name}`;

const cartBase = `import type { Product } from '../lib/catalog';

export interface CartLine {
  product: Product;
  quantity: number;
}

export class Cart {
  private readonly lines: CartLine[] = [];

  add(product: Product, quantity = 1): void {
    const line = this.lines.find((l) => l.product.id === product.id);
    if (line) {
      line.quantity += quantity;
    } else {
      this.lines.push({ product, quantity });
    }
  }

  remove(productId: string): void {
    const index = this.lines.findIndex((l) => l.product.id === productId);
    if (index >= 0) {
      this.lines.splice(index, 1);
    }
  }

  get items(): readonly CartLine[] {
    return this.lines;
  }
}
`;

const cartHead = `import type { Product } from '../lib/catalog';

export interface CartLine {
   product: Product;
   quantity: number;
}

export class Cart {
   private readonly lines: CartLine[] = [];

   add(product: Product, quantity = 1): void {
      if (quantity < 1) {
         throw new RangeError('A cart line needs at least one item.');
      }

      const line = this.lines.find((l) => l.product.id === product.id);
      if (line) {
         line.quantity += quantity;
      } else {
         this.lines.push({ product, quantity });
      }
   }

   remove(productId: string): void {
      const index = this.lines.findIndex((l) => l.product.id === productId);
      if (index >= 0) {
         this.lines.splice(index, 1);
      }
   }

   get items(): readonly CartLine[] {
      return this.lines;
   }
}
`;

const totalsBase = `import type { CartLine } from './cart';

export function subtotal(lines: readonly CartLine[]): number {
  return lines.reduce((sum, line) => sum + line.product.price * line.quantity, 0);
}

export function total(lines: readonly CartLine[], taxRate: number): number {
  return subtotal(lines) * (1 + taxRate);
}
`;

const totalsHead = `import type { CartLine } from '../checkout/cart';

export function subtotal(lines: readonly CartLine[]): number {
  return lines.reduce((sum, line) => sum + line.product.price * line.quantity, 0);
}

export function total(lines: readonly CartLine[], taxRate: number): number {
  return Math.round(subtotal(lines) * (1 + taxRate) * 100) / 100;
}
`;

// The payment step at a head: `emptyMessage` on line 14 and `gatewayMessage` on line 19, which the push changes.
const payment = (emptyMessage: string, gatewayMessage: string) => `import type { Cart } from './cart';
import { total } from '../pricing/totals';

export type PaymentResult = { ok: true; receipt: string } | { ok: false; message: string };

export interface PaymentGateway {
   charge(amount: number, token: string): Promise<{ id: string } | { error: string }>;
}

/** Charges the cart's total to the card that the token stands for. */
export async function pay(cart: Cart, token: string, gateway: PaymentGateway, taxRate = 0.25): Promise<PaymentResult> {
   const amount = total(cart.items, taxRate);
   if (amount <= 0) {
      return { ok: false, message: '${emptyMessage}' };
   }

   const result = await gateway.charge(amount, token);
   if ('error' in result) {
      return { ok: false, message: ${gatewayMessage} };
   }

   return { ok: true, receipt: result.id };
}
`;

const legacyPayment = `import type { Cart } from './cart';

// Posts the order to the old payment form and waits for its redirect.
export function payWithForm(cart: Cart, form: HTMLFormElement): void {
  form.elements.namedItem('amount')?.setAttribute('value', String(cart.items.length));
  form.submit();
}
`;

// A client that a tool writes from the gateway's API description, which the checkout plan marks as generated.
const gatewayTypes = [
   '// Generated by openapi-typescript from gateway.yaml. Do not edit.',
   '',
   'export interface paths {',
   "   '/charges': {",
   "      post: operations['createCharge'];",
   '   };',
   '}',
   '',
   'export interface components {',
   '   schemas: {',
   '      ChargeRequest: {',
   '         amount: number;',
   '         token: string;',
   '      };',
   '      Charge: {',
   '         id: string;',
   '      };',
   '      ChargeError: {',
   '         error: string;',
   '         code?: string;',
   '      };',
   '   };',
   '}',
   '',
   'export interface operations {',
   '   createCharge: {',
   "      requestBody: { content: { 'application/json': components['schemas']['ChargeRequest'] } };",
   '      responses: {',
   "         201: { content: { 'application/json': components['schemas']['Charge'] } };",
   "         402: { content: { 'application/json': components['schemas']['ChargeError'] } };",
   '      };',
   '   };',
   '}',
   '',
].join('\n');

// A file whose only change follows the move of the totals, which the checkout plan marks as mechanical.
const receipt = (totalsImport: string) =>
   `import type { Cart } from './cart';\nimport { total } from '${totalsImport}';\n\n/** Returns the receipt line with the cart's total. */\nexport function receiptTotal(cart: Cart, taxRate: number): string {\n  return \`Total: \${total(cart.items, taxRate).toFixed(2)}\`;\n}\n`;

// The order service, so the demo shows a C# diff and its highlighting.
const orderService = (minimum: string) =>
   [
      'using System;',
      'using System.Collections.Generic;',
      '',
      'namespace Acme.Orders;',
      '',
      '/// <summary>Accepts the orders that the storefront posts.</summary>',
      'public sealed class OrderService',
      '{',
      `    private const decimal MinimumTotal = ${minimum};`,
      '    private readonly List<Order> _orders = new();',
      '',
      '    public int Count => _orders.Count;',
      '',
      '    // Rejects an order below the minimum, as the checkout does.',
      '    public bool TryAccept(Order order, out string? error)',
      '    {',
      '        if (order.Total < MinimumTotal)',
      '        {',
      '            error = $"An order must total at least {MinimumTotal:C}.";',
      '            return false;',
      '        }',
      '',
      '        _orders.Add(order with { AcceptedAt = DateTime.UtcNow });',
      '        error = null;',
      '        return true;',
      '    }',
      '}',
      '',
      'public sealed record Order(string Id, decimal Total, DateTime? AcceptedAt = null);',
      '',
   ].join('\n');

const deployScript = ['#!/bin/sh', 'set -eu', 'npm ci', 'npm run build', 'rsync -a dist/ deploy@shop.example:/srv/storefront/', ''];

const searchBase = "export interface Filters {\n  category?: string;\n}\n\nexport function toQuery(filters: Filters): string {\n  return filters.category ? `category=${filters.category}` : '';\n}\n";

const searchHead =
   'export interface Filters {\n  category?: string;\n  inStock?: boolean;\n  maxPrice?: number;\n}\n\nexport function toQuery(filters: Filters): string {\n  const params = new URLSearchParams();\n' +
   "  if (filters.category) params.set('category', filters.category);\n  if (filters.inStock) params.set('inStock', 'true');\n  if (filters.maxPrice !== undefined) params.set('maxPrice', String(filters.maxPrice));\n" +
   '  return params.toString();\n}\n';

/**
 * The one repository of every sample tour, at the pull request's target and its first head, and the push that makes its second head. Every pinned tour but the checkout tour pins the second
 * head, because a world's platform reports one pull request for every URL, so a tour on other commits would find that pull request a new iteration of its own.
 *
 * The change has files of every kind and mark: added, modified, deleted, renamed, binary, large, line-endings-only and re-indented.
 */
export const sampleRepository: { base: Record<string, string>; head: Record<string, string | null>; push: Record<string, string> } = {
   base: {
      'README.md': '# Storefront\n\nThe Acme web shop.\n\n```sh\nnpm ci\nnpm run dev\n```\n',
      'package.json': '{\n  "name": "storefront",\n  "version": "2.3.0",\n  "private": true\n}\n',
      'assets/banner.png': binary('banner-1'),
      'assets/logo.png': binary('logo-1'),
      'data/catalog.json': products(15_000, 'cat'),
      'docs/architecture.md': '# Architecture\n\nThe shop is a single page that talks to the order service.\n',
      'docs/changes.md': '# Changes\n\n## 2.3.0\n\n- Faster product search.\n',
      'scripts/deploy.sh': deployScript.join('\r\n'),
      'services/orders/OrderService.cs': orderService('5m'),
      'src/app.ts': "import { Cart } from './checkout/cart';\n\nexport const cart = new Cart();\n",
      'src/checkout/cart.ts': cartBase,
      'src/checkout/legacyPayment.ts': legacyPayment,
      'src/checkout/receipt.ts': receipt('./totals'),
      'src/checkout/shipping.ts': shippingRates(null),
      'src/checkout/totals.ts': totalsBase,
      'src/email/orderConfirmation.ts': 'export function subject(orderId: string): string {\n  return `Order ${orderId}`;\n}\n',
      'src/lib/catalog.ts': 'export interface Product {\n  id: string;\n  name: string;\n  price: number;\n}\n',
      'src/search/filters.ts': searchBase,
      'src/search/results.ts': "export function emptyMessage(): string {\n  return 'No products match.';\n}\n",
   },
   head: {
      'package.json': '{\n  "name": "storefront",\n  "version": "2.4.0",\n  "private": true\n}\n',
      'assets/logo.png': binary('logo-2'),
      'data/products.json': products(16_000, 'prd'),
      'scripts/deploy.sh': deployScript.join('\n'),
      'services/orders/OrderService.cs': orderService('10.00m'),
      'src/api/gateway.generated.ts': gatewayTypes,
      'src/checkout/cart.ts': cartHead,
      'src/checkout/legacyPayment.ts': null,
      'src/checkout/payment.ts': payment('The cart is empty.', 'result.error'),
      'src/checkout/receipt.ts': receipt('../pricing/totals'),
      'src/checkout/shipping.ts': shippingRates([46, 61]),
      'src/checkout/totals.ts': null,
      'src/email/orderConfirmation.ts': 'export function subject(orderId: string): string {\n  return `Your Acme order ${orderId} is confirmed`;\n}\n',
      'src/pricing/totals.ts': totalsHead,
      'src/search/filters.ts': searchHead,
      'src/search/results.ts': "export function emptyMessage(): string {\n  return 'No products match these filters.';\n}\n",
      'tests/search/filters.test.ts': "import { toQuery } from '../../src/search/filters';\n\ntest('in stock', () => expect(toQuery({ inStock: true })).toBe('inStock=true'));\n",
   },
   // The push changes the lines of the payment and gateway stops' primary passages and of the thread on the empty-cart check, and nothing that another stop points at.
   push: { 'src/checkout/payment.ts': payment('The cart is empty, so there is nothing to charge.', 'result.error.trim()') },
};

/** The checkout tour's stops, by key, in plan order: every kind of stop, one with every section and others with few. */
const checkout = {
   overview: {
      headline: 'The checkout charges the card itself now, through a **payment gateway**, and the old payment form is gone.',
      how: 'The cart computes its total with [the pricing module](ref:head/src/pricing/totals.ts#L7-9), and [`pay`](ref:head/src/checkout/payment.ts#L11-11) sends that total to the gateway.\n\nThe old form post is [deleted](ref:base/src/checkout/legacyPayment.ts#L3-7).',
      why: "The payment form could not report declined cards back to the shop. The gateway's [charge reference](https://gateway.example/docs/charges) lists every answer it gives.",
   },
   totals: {
      headline: 'Totals moved from the checkout to a pricing module, and the total now rounds to cents.',
      passages: [
         ['head', 'src/pricing/totals.ts', 7, 9],
         ['base', 'src/checkout/totals.ts', 7, 9, 'before the move'],
         ['head', 'src/lib/catalog.ts', 1, 5, 'the product type'],
         ['head', 'data/catalog.json', 9000, 9000, 'a priced product'],
      ],
      why: 'The payment step needs the total, and it should *not* import the cart internals to get it. Each cart line holds [a product](ref:head/src/lib/catalog.ts#L1-5), whose type stays as it was.',
   },
   payment: {
      headline: 'The new `pay` function charges the cart total and turns the gateway answer into a result.',
      passages: [
         ['head', 'src/checkout/payment.ts', 11, 23],
         ['head', 'src/checkout/cart.ts', 12, 14, 'quantity check'],
         ['head', 'src/pricing/totals.ts', 7, 9, 'the total'],
         ['base', 'src/checkout/shipping.ts', 52, 57, 'regions it stops shipping to'],
      ],
      how: "It refuses an empty cart before it calls the gateway, so the gateway is never asked to charge nothing:\n\n```ts\nconst amount = total(cart.items, taxRate);\nif (amount <= 0) {\n   return { ok: false, message: 'The cart is empty.' };\n}\n```",
      why: 'The gateway answers a charge with [one of two bodies](ref:head/src/api/gateway.generated.ts#L28-31), and `pay` turns each into a result.',
      designChoices:
         'A result union for every outcome the customer can see:\n\n1. `ok: true` with the receipt.\n2. `ok: false` with a message for the customer.\n\nA thrown error is left for faults in the shop itself,  \nsuch as a gateway that is not configured.',
      pinnedBy: 'No test calls `pay` yet. The team wiki has [a test plan](http://wiki.example/payments/tests), whose plain HTTP link shows as text.',
      tags: ['public-api'],
   },
   gateway: {
      title: 'Gateway errors reach the customer as they are',
      headline: 'The gateway message reaches the customer as the gateway wrote it.',
      passages: [
         ['head', 'src/checkout/payment.ts', 17, 20],
         ['head', 'src/checkout/payment.ts', 6, 8, 'the gateway answer'],
         ['head', 'src/api/gateway.generated.ts', 18, 21, 'the error body'],
      ],
      scenario: 'The gateway answers with an internal message, such as a fraud score, and the checkout page shows it to the customer.',
      how: '`pay` copies `result.error` into [the result message](ref:head/src/checkout/payment.ts#L17-20).',
      why: "The gateway messages are written for the shop, not for customers. They can hold:\n\n- a fraud score\n- the name of the bank's rule that declined the card\n- an internal error code",
      designChoices: 'The message passes unchanged, so the shop can log what the gateway said.',
      pinnedBy: 'Nothing pins the message text.',
      checks: [
         {
            key: 'fixed-message',
            question: 'Should the checkout show a fixed message and **log** the gateway one?',
            recommendation: 'Yes. Show a fixed message, and log [the gateway answer](ref:head/src/checkout/payment.ts#L6-8) for support.',
            suggestions: [
               { text: 'Show a fixed message and log the gateway one.', type: 'decision' },
               { text: 'A fixed message hides why a card was declined.', type: 'concern' },
               { text: 'Which messages does support need to see?', type: 'openQuestion' },
            ],
         },
         { key: 'safe-messages', question: 'Does the gateway document which messages are safe to show? See <https://gateway.example/docs/errors>.' },
      ],
      tags: ['security'],
   },
   // One file, few words and no checks beyond these, so the mixed rule shows it inline in an inline diff.
   cartLine: {
      headline: '`add` refuses a quantity below one, so the cart never holds an empty or a negative line.',
      passages: [['head', 'src/checkout/cart.ts', 12, 14]],
      how: 'The check throws a `RangeError` before [the line lookup](ref:head/src/checkout/cart.ts#L16-16), so a refused add leaves the cart as it was.',
      why: '`pay` charges [the cart total](ref:head/src/pricing/totals.ts#L7-9), which a negative quantity would lower.',
      checks: [
         {
            key: 'zero-quantity',
            question: 'Should a quantity of zero remove the line instead of throwing?',
            suggestions: [
               { text: 'Keep the throw.', type: 'decision' },
               { text: 'A zero from the quantity box would crash the page.', type: 'concern' },
            ],
         },
         { key: 'cart-limit', question: 'Does the cart need an upper limit on the quantity?' },
         { key: 'line-merge', question: 'Should two adds of the same item merge into one line?' },
      ],
   },
} satisfies Record<string, SampleContent>;

/** The first versions of the checkout tour's two revised stops: the payment stop with a shorter headline, and the gateway stop with a check that its revision drops. */
const checkoutFirstVersions = {
   payment: { ...checkout.payment, headline: 'The new `pay` function charges the cart total.' },
   gateway: {
      headline: 'The gateway message reaches the customer.',
      passages: checkout.gateway.passages,
      scenario: checkout.gateway.scenario,
      how: '`pay` passes the gateway message on.',
      checks: [{ key: 'retired', question: 'Should the checkout hide every gateway message?' }, checkout.gateway.checks[0]!],
   },
} satisfies Record<string, SampleContent>;

/** The revision of the checkout tour's cart line stop that a test writes: a new `how`, which outdates the thread on it, the limit check dropped and the merge check under a new key. */
export const cartLineRevision: SampleContent = {
   ...checkout.cartLine,
   how: 'The check throws a `RangeError` before it looks the line up, so a refused add changes nothing.',
   checks: [checkout.cartLine.checks[0]!, { key: 'line-quantity', question: 'Should an add of an item already in the cart raise its quantity?' }],
};

/** The totals stop with a `how` that explains the rounding, as the checkout tour's waiting explain action asks. */
export const explainedTotals: SampleContent = {
   ...checkout.totals,
   how: "The total adds the lines' prices and the tax, then rounds once, to cents: [`total`](ref:head/src/pricing/totals.ts#L7-9) multiplies by 100, rounds, and divides again, so every caller charges the same amount.",
};

/** The synthesis that the checkout tour holds, which Claude wrote before the reviewer skipped a stop, so it predates the summary. */
export const earlierSynthesis =
   'The review settled the rounding: totals round once, in [the pricing module](ref:head/src/pricing/totals.ts#L7-9). The gateway errors still worry you, and whether anything posts the old payment form stays open.';

/** The reply that answers the checkout tour's received question, with a `ref:` link labeled "the form". */
export const oldFormAnswer = 'Only the checkout page posted it, and [the form](ref:base/src/checkout/legacyPayment.ts#L4-7) is gone with this change.';

/** A one-stop plan with an overview, for the tours whose tour list state matters more than their content. */
function overviewOnly(headline: string): SampleTour['chapters'] {
   return [{ title: 'The change', stops: [{ key: 'overview', title: 'The storefront release', kind: 'overview', content: { headline } }] }];
}

/** Returns the sample tours, oldest first, which is the order the template creates them in. */
export function sampleTours(): SampleTour[] {
   return [
      {
         key: 'beta-flag',
         number: 372,
         ageMinutes: 60 * 24 * 14,
         familiarity: 'familiar',
         pin: 'pinned',
         pullRequestState: 'abandoned',
         chapters: overviewOnly('The beta flag goes, and every page shows the new layout.'),
      },
      {
         key: 'footer-links',
         number: 377,
         ageMinutes: 60 * 24 * 11,
         familiarity: 'some',
         pin: 'pinned',
         pullRequestState: 'completed',
         chapters: overviewOnly('The footer links point at the new help pages.'),
         steps: [{ progress: 'summary' }],
      },
      {
         key: 'legacy-banner',
         number: 380,
         ageMinutes: 60 * 24 * 9,
         familiarity: 'new',
         pin: 'pinned',
         chapters: overviewOnly('The legacy banner is gone from the home page.'),
         steps: [{ progress: 'overview' }, { archive: true }],
      },
      { key: 'docs-typos', number: 394, ageMinutes: 60 * 24 * 4, familiarity: 'familiar', pin: 'pinned', chapters: overviewOnly('Typos in the docs are fixed.'), steps: [{ progress: 'summary' }] },
      {
         key: 'readme-only',
         number: 398,
         ageMinutes: 60 * 24 * 3,
         familiarity: 'familiar',
         pin: 'pinned',
         chapters: overviewOnly('The README names the new version.'),
         // The session that creates the tour ends with the template, so the question is unanswered and the tour has no session.
         steps: [
            { ask: 'readme', anchor: { kind: 'tour' }, text: 'Is the README still accurate?' },
            { note: 'readme-links', anchor: { kind: 'tour' }, text: "Check the README's links once a session attaches." },
         ],
      },
      {
         key: 'inventory-sync',
         number: 405,
         ageMinutes: 60 * 26,
         familiarity: 'some',
         pin: { failed: 'The platform refused the access token. Sign in to the platform again, then create the tour again.' },
      },
      { key: 'cart-badge', number: 401, ageMinutes: 60 * 5, familiarity: 'some', pin: 'pinned', chapters: overviewOnly('The cart badge counts items, not lines.'), steps: [{ progress: 'overview' }] },
      checkoutTour(),
      {
         key: 'search-filters',
         number: 428,
         ageMinutes: 15,
         familiarity: 'new',
         pin: 'pinned',
         session: 'unconfirmed',
         steps: [{ markReviewed: 'src/search/results.ts' }, { markReviewed: 'src/checkout/payment.ts', atFirstHead: true }],
         live: [{ ask: 'filters-order', anchor: { kind: 'tour' }, text: 'Do the filters keep their order after a reload?' }],
      },
      {
         key: 'wishlist',
         number: 433,
         ageMinutes: 8,
         familiarity: 'some',
         planFirst: true,
         pin: 'pinned',
         session: 'confirmed',
         chapters: [
            {
               title: 'The new filters',
               stops: [
                  {
                     key: 'what',
                     title: 'What the filters add',
                     kind: 'overview',
                     later: { headline: 'Search can now filter by stock and by price, besides the category.', how: 'Each filter becomes one query parameter when it is set.' },
                  },
                  {
                     key: 'query',
                     title: 'Building the query string',
                     kind: 'inspect',
                     anchorHint: { path: 'src/search/filters.ts', line: 7 },
                     later: { headline: '`toQuery` builds the query with `URLSearchParams`, one parameter per filter that is set.', passages: [['head', 'src/search/filters.ts', 7, 13]] },
                  },
               ],
            },
            {
               title: 'Open points',
               stops: [
                  {
                     key: 'empty',
                     title: 'The message for no results',
                     kind: 'inspect',
                     anchorHint: { path: 'src/search/results.ts', line: 2 },
                     later: { headline: 'The empty result now names the filters.', passages: [['head', 'src/search/results.ts', 2, 2]] },
                  },
                  {
                     key: 'price',
                     title: 'Should an empty price filter be sent?',
                     kind: 'question',
                     anchorHint: { path: 'src/search/filters.ts', line: 12 },
                     later: {
                        headline: 'A price filter of zero is sent, because `toQuery` checks for undefined.',
                        passages: [['head', 'src/search/filters.ts', 12, 12]],
                        checks: [{ key: 'zero-price', question: 'Should a price filter of zero mean no filter?' }],
                     },
                  },
               ],
            },
         ],
      },
   ];
}

/**
 * The checkout tour, pinned to the first head with the push as its available iteration. Its threads hold a reply of each resolution, a decline without words, an unanswered question, a canceled
 * one, and a thread at each kind of place: lines at the head, base lines that an inline diff shows as deleted, a file, text in a stop, text in a check's recommendation, a stop and the tour.
 * Several threads are resolved. Claude started two threads, one of them outdated, and added a message to an answered thread. A revision of the gateway stop outdated the thread on the text of
 * its first version and the answer to the check it dropped. Its tour actions are a resolved revise, replan and reorder, beside a resolved change of the familiarity level. Each world adds a
 * received question, a follow-up that waits and an explain that waits, which no template keeps.
 */
function checkoutTour(): SampleTour {
   return {
      key: 'checkout-rework',
      number: 412,
      ageMinutes: 90,
      familiarity: 'familiar',
      pin: 'firstHead',
      session: 'confirmed',
      fileMarks: [
         { path: 'src/api/gateway.generated.ts', mark: 'generated', reason: "openapi-typescript writes it from gateway.yaml, the gateway's API description." },
         { path: 'src/checkout/receipt.ts', mark: 'mechanical', reason: 'Only the import path changed, because the totals moved to src/pricing.' },
      ],
      chapters: [
         {
            title: 'What the change does',
            stops: [
               { key: 'overview', title: 'The checkout at a glance', kind: 'overview', content: checkout.overview },
               { key: 'totals', title: 'Where totals come from', kind: 'context', content: checkout.totals },
            ],
         },
         {
            title: 'Paying',
            stops: [
               { key: 'payment', title: 'The new payment step', kind: 'inspect', content: checkoutFirstVersions.payment },
               { key: 'gateway', title: 'Gateway errors reach the customer', kind: 'risk', content: checkoutFirstVersions.gateway },
               { key: 'cartLine', title: 'A cart line needs at least one item', kind: 'inspect', content: checkout.cartLine },
            ],
         },
         {
            title: 'Before you approve',
            stops: [
               {
                  key: 'oldForm',
                  title: 'Does anything still post the old payment form?',
                  kind: 'question',
                  anchorHint: { path: 'src/checkout/legacyPayment.ts' },
                  later: {
                     headline: 'The old payment form is deleted. Check that no page still posts to it.',
                     passages: [['base', 'src/checkout/legacyPayment.ts', 3, 7]],
                     why: 'A page that still posts [the form](ref:base/src/checkout/legacyPayment.ts#L4-7) would send its customers to a payment page that is gone.',
                     checks: [
                        {
                           key: 'old-form',
                           question: 'Does any template or script outside this repository post the old form?',
                           recommendation: 'Search the storefront templates for `legacyPayment` before you approve.',
                           suggestions: [
                              { text: 'Nothing posts it any more.', type: 'decision' },
                              { text: 'The marketing pages may still post it.', type: 'concern' },
                           ],
                        },
                     ],
                  },
               },
               {
                  key: 'productData',
                  title: 'The product data file',
                  kind: 'inspect',
                  anchorHint: { path: 'data/products.json' },
                  later: { headline: 'The product data moves to a file of its own, with a new ID prefix.', passages: [['head', 'data/products.json', 1, 3]] },
               },
            ],
         },
      ],
      steps: [
         { ask: 'gateway-passes', anchor: { kind: 'stopText', stop: 'gateway', field: 'how', text: 'passes the gateway message on' }, text: 'Is the message passed on as it is?' },
         { reply: 'gateway-passes', resolution: 'declined' },
         { answer: ['gateway', 'retired'], text: 'An answer to a check that Claude dropped.', type: 'decision' },
         { action: 'revise-payment', request: { kind: 'revise', stop: 'payment' }, text: 'Say in the headline what pay returns.' },
         { write: 'payment', content: checkout.payment },
         { reply: 'revise-payment', resolution: 'done', text: 'Revised the headline: `pay` turns the gateway answer into a result.' },
         { resolve: 'revise-payment' },
         { write: 'gateway', content: checkout.gateway },
         { ask: 'totals', anchor: { kind: 'stop', stop: 'totals' }, text: 'Why does the total round to cents here and not in `pay`?' },
         {
            reply: 'totals',
            resolution: 'done',
            text: 'Rounding once, in [`total`](ref:head/src/pricing/totals.ts#L7-9), means every caller charges the same amount. `pay` only passes that amount on.',
         },
         { resolve: 'totals' },
         { ask: 'tax', anchor: { kind: 'stop', stop: 'totals' }, text: 'Should the total include tax on shipping?' },
         { reply: 'tax', resolution: 'needsAnswer', text: "Which country's rules should the total follow?" },
         { followUp: 'tax', text: 'Sweden, for now.' },
         { reply: 'tax', resolution: 'done', text: 'Then shipping carries the VAT rate of the goods, so the total already includes it.' },
         { resolve: 'tax' },
         { ask: 'work-item', anchor: { kind: 'tour' }, text: 'Create a work item for the missing payment tests.' },
         { reply: 'work-item', resolution: 'declined' },
         { resolve: 'work-item' },
         { ask: 'coupons', anchor: { kind: 'tour' }, text: 'Do coupons still apply after the rework?' },
         { cancel: 'coupons' },
         { ask: 'currency', anchor: { kind: 'tour' }, text: 'Which currency does the total use?' },
         { reply: 'currency', resolution: 'done', text: "The cart's own currency. Every line of a cart has the same one." },
         { ask: 'discount', anchor: { kind: 'stop', stop: 'totals' }, text: 'Is the discount applied before tax?' },
         { reply: 'discount', resolution: 'done', text: 'Before tax, so the tax is charged on the lower price.' },
         { addMessage: 'discount', text: 'Folded into the revised stop: its *how* now says when the discount applies.' },
         {
            startThread: 'claude-currency',
            anchor: { kind: 'code', side: 'head', path: 'src/pricing/totals.ts', startLine: 7, endLine: 9 },
            text: 'A line in another currency reaches [`total`](ref:head/src/pricing/totals.ts#L7-9) unchecked, so the sum mixes currencies. Should the cart refuse such a line?',
         },
         { startThread: 'claude-refunds', anchor: { kind: 'tour' }, text: 'While planning, I found no test of a refund. Refunds are outside this change, so the tour leaves them out.' },
         { outdate: 'claude-refunds' },
         { action: 'replan', request: { kind: 'replan' }, text: 'Add a stop on the quantity checks.' },
         { reply: 'replan', resolution: 'done', text: 'Kept the plan: the cart line stop already covers the quantity checks.' },
         { resolve: 'replan' },
         { familiarity: 'familiarity', level: 'some' },
         { reply: 'familiarity', resolution: 'done', text: 'Rewrote the stops after the one you view for your new familiarity level.' },
         { resolve: 'familiarity' },
         { action: 'reorder', request: { kind: 'reorder', stops: ['overview', 'totals', 'gateway', 'payment', 'cartLine', 'oldForm', 'productData'] }, text: 'Risks first.' },
         {
            reply: 'reorder',
            resolution: 'done',
            text: 'Kept the order: the payment stops rely on how totals come about, so the risk reads best after them.',
         },
         { resolve: 'reorder' },
         { ask: 'message', anchor: { kind: 'stop', stop: 'gateway' }, text: 'Can you change the checkout to show a fixed message?' },
         { newSession: true },
         { resend: 'message' },
         { reply: 'message', resolution: 'needsAnswer', text: 'Which message should the customer see, and should declined cards get one of their own?' },
         // These wait when the template is sealed, so each world's start leaves them unanswered.
         { ask: 'old-total', anchor: { kind: 'code', side: 'base', path: 'src/checkout/totals.ts', startLine: 8, endLine: 8 }, text: 'Did the old total round at all?' },
         {
            ask: 'rounding',
            anchor: { kind: 'code', side: 'head', path: 'src/pricing/totals.ts', startLine: 7, endLine: 9 },
            text: 'Why multiply by 100 and divide again, when toFixed would round too?',
         },
         { ask: 'payment-tests', anchor: { kind: 'file', side: 'head', path: 'src/checkout/payment.ts' }, text: 'Should this file have tests of its own?' },
         { ask: 'negative-total', anchor: { kind: 'code', side: 'head', path: 'src/checkout/payment.ts', startLine: 13, endLine: 15 }, text: 'Can a refund make the total negative here?' },
         {
            ask: 'result-union',
            anchor: { kind: 'stopText', stop: 'payment', field: 'designChoices', text: 'A result union for every outcome the customer can see' },
            text: 'Would a thrown error be simpler here?',
         },
         { ask: 'refused-add', anchor: { kind: 'stopText', stop: 'cartLine', field: 'how', text: 'a refused add leaves the cart as it was' }, text: 'Does the page show the refusal?' },
         { ask: 'log-answer', anchor: { kind: 'stopText', stop: 'gateway', field: 'checks[fixed-message].recommendation', text: 'for support' }, text: 'Can support read that log?' },
         { ask: 'free-items', anchor: { kind: 'stop', stop: 'payment' }, text: 'Is the empty-cart check enough for a cart with only free items?\nA free sample costs 0.' },
         // A batch that Claude fetched, then one that it never fetches, so each world holds a received answer and two whose session ended.
         { answer: ['cartLine', 'zero-quantity'], text: 'A zero from the quantity box would crash the page.', type: 'concern' },
         { sendAnswers: true },
         { fetchAnswers: true },
         { answer: ['cartLine', 'cart-limit'], text: 'No limit, the warehouse checks the stock.', type: 'decision' },
         { answer: ['cartLine', 'line-merge'], text: 'Which does the order page expect?', type: 'openQuestion' },
         { sendAnswers: true },
         { answer: ['gateway', 'fixed-message'], text: 'A fixed message hides why a card was declined.', type: 'concern' },
         { note: 'gateway-retry', anchor: { kind: 'stop', stop: 'gateway' }, text: 'Does a gateway timeout retry the payment, or charge twice?', stop: 'gateway' },
         { note: 'coupon-order', anchor: { kind: 'tour' }, text: 'Ask whether coupons apply before or after rounding.' },
         { synthesis: earlierSynthesis },
         { skip: 'productData' },
      ],
      live: [
         { ask: 'old-form', anchor: { kind: 'tour' }, text: 'Which pages still link to the <form> of the old payment step?' },
         { fetchQuestion: 'old-form' },
         { followUp: 'currency', text: 'What happens when a line in another currency is added?' },
         { action: 'explain-totals', request: { kind: 'explain', stop: 'totals' } },
      ],
   };
}

/** The tour whose pin is in progress, which a test creates while the world's platform holds its answer. */
export const pinningTour: SampleTour = { key: 'order-emails', number: 431, ageMinutes: 0, familiarity: 'familiar', pin: 'pinning' };
