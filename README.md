# PayPal Currency Module for OpenSimulator

A currency module for [OpenSimulator](http://opensimulator.org/), updated to work with OpenSim 0.9.3 (core) as used in
[DreamGrid](https://outworldz.com/). Originally based on [DTL-PayPal](https://github.com/AdamFrisby/DTL-PayPal) by
DeepThink Pty Ltd, with a number of bug fixes and security improvements ported from
[SnoopyPfeffer/Mod-PayPal](https://github.com/SnoopyPfeffer/Mod-PayPal) (see
[the original writeup](https://snoopypfeffer.wordpress.com/2009/11/18/paypal-money-module/) for background). DreamGrid
now also supports enabling and configuring this module directly in its UI.

---
**WARNING: real money.** This module uses PayPal as the backend, meaning every transaction is a real, hard transaction
in USD. Use PayPal's sandbox mode to test before going live - see below.

---

## What it does

Any user with a PayPal account can send money to another avatar, an object, or a group. The grid owner or region
owner fills in which avatar names (or group UUIDs) can *receive* funds and their PayPal email address; anyone can
*send* funds to those recipients.

- **User-to-user payments**, **user-to-object payments**, **user-to-object purchases**, and **user-to-land
  purchases** are all supported.
- The default exchange rate is **OS$1 = US$0.01** (e.g. OS$300 = US$3.00).
- User-to-user payments are confirmed with instant messages to both the sender and the receiver, but only while
  they're online at the time. Enable DreamGrid's email system to also send offline users an email with the same
  notice.
- Group-owned objects and land can receive PayPal payments too (default: off) - each group that should receive funds
  needs its own PayPal email address configured, keyed by group UUID.
- The LSL script function `llGiveMoney` is **not supported** by this module - it will simply fail (objects can't
  give PayPal money out, only receive it).

## How a payment works

1. **Negotiate the amount.** This is just filling in the normal in-world "Pay" or "Buy" dialog on a vendor, object,
   or avatar that has been set up to receive PayPal payments - for example, someone taking tips for a performance.
   Once configured, payers don't need to click anything special; the usual pay/buy dialog just works.
2. **Confirm on PayPal.** You'll be sent to a web page (linking to PayPal's own checkout) that sets up and completes
   the transaction. From here it works exactly like any other PayPal purchase.
3. **The region and PayPal confirm the transaction.** PayPal notifies the region server directly (via PayPal's IPN
   mechanism) once your payment clears.
4. **The recipient receives the funds**, and the purchased item (if any) is delivered.

## Configuring this module

Add the following to your `OpenSim.ini`:

```ini
[Economy]   ; (or [Startup] - not both, see "Selecting PayPal as the active currency module" below)
economymodule = PayPal

[PayPal]
Enabled = true

; Use www.sandbox.paypal.com to test without moving real money, www.paypal.com for production.
PayPalURL = www.paypal.com

; Fetch email addresses from the grid's Users service automatically, for recipients with no explicit
; entry below. Default is off - see "Security notes" below before turning this on.
AllowGridEmails = false

; Allow group-owned objects/land to receive payments via [PayPal Groups] below. Default is off.
AllowGroups = false

; Optional: reject a payment/purchase up front (with a clear in-viewer message) if it's below this many US
; dollars, instead of letting the buyer hit a confusing error on PayPal's own checkout page for amounts too
; small to cover PayPal's own transaction fees. 0 (the default) means no minimum is enforced.
MinimumAmount = 0

[PayPal Users]
Avatar Name=paypal@email.com
Other Avatar=another.paypal@email.com

[PayPal Groups]
a683cc8a-a5cc-4c40-87bc-ebcfbcfb1456=mygroup.account@yahoo.com
```

### `[PayPal Users]`

One line per recipient - the avatar name, or a group UUID (see `[PayPal Groups]` below) - mapped to the PayPal email
address that avatar's funds should be sent to. **Avatars not listed here cannot receive funds** (though anyone can
still *send* funds to a listed recipient). This is also where you'd list, for example, a performer who wants to
collect tips - once they're set up, payers can just use the normal Pay dialog.

### `[PayPal Groups]`

Only consulted when `AllowGroups = true`. One line per group, keyed by the group's UUID, mapping to the PayPal email
address that group-owned objects/land should pay out to.

### Security notes

- User email addresses can also be fetched automatically from the grid's Users service if `AllowGridEmails = true`.
  Fetched addresses are cached by the region server until it restarts. **Locally-defined addresses in `[PayPal
  Users]` always take precedence** over a grid-fetched address, for security.
- Use locally-defined `[PayPal Users]` entries (not `AllowGridEmails`) for anyone receiving significant amounts
  within a region - shop owners, for example - since that's meaningfully more secure than relying on a fetched grid
  profile email. It's also a good place to point high-volume sellers at a dedicated PayPal micropayments account
  (see below) instead of a standard account used elsewhere.

## Selecting PayPal as the active currency module

Only one money module should ever be active on a given region (PayPal, Gloebit, or the free
`BetaGridLikeMoneyModule`) - if more than one registers itself, OpenSim uses whichever happens to load first, which
isn't something you want to leave to chance. To make sure PayPal is the one that wins, set `economymodule = PayPal`
in **either** `[Startup]` or `[Economy]` (not both - if they disagree, neither module will consider itself selected).
PayPal only activates when *both* `economymodule = PayPal` and `[PayPal] Enabled = true` are set; if you only set
`[PayPal] Enabled` and leave `economymodule` unset or pointing elsewhere, PayPal will log that it was not selected
and stay inactive.

## Transaction locking

While an object has a PayPal payment or purchase in progress (buyer sent to PayPal's checkout, IPN not yet received),
a second buy/pay attempt on that same object is rejected with an in-viewer message rather than starting a second,
concurrent transaction. If a buyer never completes PayPal's checkout, the lock is automatically released after 5
minutes.

## Micropayments

PayPal offers Micropayments pricing to Business and Premier merchant accounts for same-country transactions (US to
US, GB to GB, AU to AU, EU to EU) - a special rate of 5% + $0.05 per transaction, aimed at merchants processing small
transactions (usually under $10). You must qualify for the program; fees for other currency pairs vary but are also
comparatively low.

If you'll be receiving both regular and micro payments, PayPal recommends maintaining **two separate accounts**: your
normal (parent) account for standard-rate transactions, and a second account enrolled in Micropayments for your
small transactions - since each PayPal account is associated with only one merchant processing rate. See PayPal's own
documentation on their current fees for details.

## Known limitations

- No LSL-scriptable hook lets an in-world vendor script react to (or lock itself during) an in-progress PayPal
  transaction.
- `llGiveMoney` is not supported - see above.
