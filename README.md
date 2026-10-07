# Till: an offline-first point of sale in C#

[![Test and deploy](https://github.com/hamzamehmood46/point-of-sale-dotnet/actions/workflows/pages.yml/badge.svg)](https://github.com/hamzamehmood46/point-of-sale-dotnet/actions/workflows/pages.yml)

**Live demo: [hamzamehmood46.github.io/point-of-sale-dotnet](https://hamzamehmood46.github.io/point-of-sale-dotnet/)**

A working register that keeps selling when the internet drops, then delivers every sale to the back office **exactly once**. Ring up sales, split payments, give change, discount, refund, and close the shift with the cash drawer balancing to the cent.

The whole point-of-sale engine is plain C# with no UI and no clock of its own, so it can be tested hard. Blazor WebAssembly runs that same engine in your browser, so the demo needs no server.

## Try it (30 seconds)

1. Press **Online** to go offline, then ring up two or three sales.
2. Open **Back office**: it has received nothing, but the sales are safe on the register.
3. Switch on **Drop the next reply**, then go back online. The server stores the first sale, the connection "drops" before the register hears back, and the register sends it again. Watch the back office block the duplicate and the books still match to the cent.
4. Make an offline sale and **reload the page** before reconnecting. The queued sale is still there and is delivered once.

## What it does

| Area | Behavior |
|---|---|
| Money | Whole cents (`long`), never floating point. Tax rounds half away from zero. |
| Pricing | Line discounts, then a whole-sale discount shared across lines in proportion (largest-remainder, so the cents balance exactly), then tax per line. Three tax classes: standard, reduced, exempt. |
| Payment | Cash with change, card, or both. Cards are only charged once everything else is known to work, and an approved charge is released if a later one is declined. Tips. |
| Idempotency | Every sale carries a key. Pressing "Complete" twice, or retrying, returns the original sale: one sale, one charge, one stock deduction. |
| Stock | Selling more than is on hand is refused. Refunds put goods back. |
| Refunds | Whole or partial. You cannot refund more than was sold, a card refund cannot exceed what was charged to the card, tips are not refunded, and refunding the last units returns exactly what is left so rounding never leaves a stray cent. |
| Shifts | Opening float, a running X report, and a close that compares counted cash with expected cash (float + cash sales − cash refunds) and reports over or short. |
| Offline sync | Sales and refunds go into an outbox first and are removed only when the back office confirms. Order is preserved, delivery stops at the first failure, and the queue is saved in the browser so a reload loses nothing. |

## How the offline sync stays correct

The classic failure: the server saves the sale, the reply is lost, the register retries, and revenue is counted twice. Here the key is created on the register at the moment of sale, so every retry of the same sale carries the same key, and the back office de-duplicates by key. The register treats "already had it" as delivered. That is the same outbox and idempotent-consumer idea as in [outbox-pattern-dotnet](https://github.com/hamzamehmood46/outbox-pattern-dotnet), applied to a till.

## How it is built

```
src/Pos.Engine   C#: money, pricing, cart, register, offline queue, receipts. No UI, no hidden clock.
src/Pos.Web      Blazor WebAssembly front end (runs the same engine in the browser, no server)
tests/           xUnit tests for the engine
```

## Tests

```bash
dotnet test
```

61 tests, including:

- exact totals for each tax class, tax rounding on the half cent, discount ordering, and a seeded property test that gross − discounts = net and net + tax = total across 300 random carts;
- cart discounts split across lines always add up to the discount (500 random splits);
- change, tips, split cash and card, declined cards releasing earlier approvals, cards not charged when the cash part is too small;
- the same key never sells twice, and receipt numbers are sequential;
- refunds that cannot exceed the sale, partial refunds that add up exactly, card-refund limits, stock restored;
- drawer reconciliation including over and short;
- sync: delivery after an outage, order preserved on partial failure, a lost reply not double-counting, refunds reducing the back-office total, the queue surviving a reload, and damaged storage not crashing the till.

Two mutation checks were done while building it: removing the "last units refund the exact remainder" rule fails one test, and making the back office store retries as new sales fails two. In other words, the suite does notice when the money or sync logic breaks.

I also drove the deployed build in a real browser: a cash sale with change, offline sales, a dropped reply, a reload during an outage, a partial refund and closing the shift. All behaved as the tests say.

## Run it locally

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/Pos.Web
```

## Scope, honestly

- The "internet" is a switch in the page and the "back office" is an in-memory simulation that de-duplicates by key. This shows the pattern working, not a production integration.
- Card payments use a stand-in terminal (any `tok_` token is approved, `tok_decline` is declined). No real payment provider, receipts are shown on screen rather than printed, and there is no authentication, taxes by jurisdiction, tax-inclusive pricing or cash rounding.
- Only the sync queue is persisted (in the browser's local storage). The day's sales list resets on reload, by design, to keep the demo simple.

## Deploying

Every push to `main` runs the tests and, if they pass, publishes the site to GitHub Pages (see `.github/workflows/pages.yml`).

## Related projects

- [Transactional Outbox and Idempotent Inbox](https://github.com/hamzamehmood46/outbox-pattern-dotnet)
- [Coffee Shop Rush](https://hamzamehmood46.github.io/coffee-shop-rush/) (an animated explainer of the same ideas)
- [Monolith vs Microservices in .NET](https://github.com/hamzamehmood46/monolith-vs-microservices-dotnet)

## License

MIT
