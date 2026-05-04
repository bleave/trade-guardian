# TradeGuardian

A discipline-enforcement **strategy** for the [Quantower](https://www.quantower.com/) platform — _"Trade Guardian + Revenge Guard."_

## What it does

Watches your live positions and trade flow, and surfaces invalidation / over-trading conditions before they wreck a session. Includes:

- Open-position detection per account / symbol scope
- Configurable exit-invalidation rules (price crossing EMA9 / EMA21, candle closes beyond either EMA, etc.)
- Bar-close vs. intrabar evaluation modes
- Hooks for revenge-trading detection (rapid re-entry after a loss)

## Use with TradeGuardianOverlay

This is the headless rules engine. Pair it with [TradeGuardianOverlay](https://github.com/bleave/TradeGuardianOverlay) on the chart to actually _see_ the warnings.

## Build

Open `TradeGuardian.slnx` in Visual Studio or `dotnet build`. Copy the DLL into Quantower's `Settings\Scripts\Strategies` folder.

## Stack

C# / .NET, Quantower BusinessLayer SDK.
