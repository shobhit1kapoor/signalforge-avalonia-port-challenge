# Avalonia Port Challenge: rules and entry checklist

The challenge is for modernizing an application that already existed before the contest opened. A port or a rewrite is accepted, but the entry must preserve the source application's identity and explain the migration honestly. SIGNALFORGE is a port of Terminal2, with separately identified original protocol-forensics capabilities and a reused terminal component.

## Official challenge terms

Source: [The Avalonia Port Challenge: $15,000 in Prizes, Funded by Devolutions](https://avaloniaui.net/blog/avalonia-port-challenge), published August 28, 2026.

- **Categories:** Best Cross-Platform Port, Best Legacy Revival, Best Everyday Tool, and Best IT Pro Tool. The same codebase on mobile or WebAssembly receives additional recognition.
- **Judging:** migration difficulty and completeness; quality of the resulting application; strength of cross-platform implementation; engineering quality; and usefulness of the migration write-up. The organizer explicitly says a small, complete port can beat a larger incomplete one.
- **Eligibility:** solo developers or teams of up to three; one prize per team. AI-assisted code is allowed. The original app must predate the contest. Rewrites count. Someone else's app can be ported only when its license allows it; closing a derivative of someone else's copyleft software is disqualifying.
- **Open-source entry:** public repository under an OSI-approved license; downloadable builds for each target platform; before-and-after screenshots; and a short migration write-up in the README.
- **Closed-source entry:** the same builds, screenshots, and write-up, and a genuinely runnable build for judges.
- **Submission:** complete the entry form linked from the official post. Deadline is **23:59 UTC on October 23, 2026**; winners are announced November 6, 2026.
- **Prize pool:** $15,000 total: $5,000 grand prize; four $2,000 category prizes; four $500 honorable mentions.

## Devolutions' role

The official challenge post says Devolutions CEO David Hervieux proposed the challenge to Avalonia and offered to fund the entire $15,000 prize pool. This prize sponsorship is separate from Devolutions' $3 million, three-year sponsorship of Avalonia's open-source development announced in 2025. Devolutions is modernizing its own Remote Desktop Manager toward native Avalonia applications, which gives the sponsor direct migration experience.

Sources: [Avalonia challenge announcement](https://avaloniaui.net/blog/avalonia-port-challenge) and [Devolutions' $3M Avalonia sponsorship announcement](https://devolutions.net/blog/devolutions-sponsors-avalonia-with-dollar3m-to-secure-native-rdm-future/).

## Reference projects

These are product and evidence benchmarks from the project brief; they are not challenge eligibility requirements and their source code is not used here.

- [Aster Terminal](https://github.com/kris70lesgo/aster-terminal) describes itself as an independent iTerm2-workflow migration to C#/Avalonia. Its current README documents independent real shells per pane using forkpty on macOS/Linux and ConPTY on Windows; tabs, horizontal/vertical splits, profiles, search, and optional tmux/Python integrations; and explicit non-goals. Release 0.14.0 links a native CI run with 123 test cases per OS, platform-specific skips, self-contained downloads, and packaged real-shell acceptance checks. It labels its iTerm2 screenshot as a reference and its Aster screenshot as an acceptance fixture, and maintains separate feature-parity, visual-parity, migration, performance, and licensing reports. These are the right evidence habits to learn from; they are not proof that SIGNALFORGE has the same functionality or platform validation.
- [Devolutions Terminal](https://github.com/Devolutions/devolutions-terminal) is Devolutions' broader Avalonia terminal product. Its repository documents .NET 10/Avalonia 12, ConPTY and forkpty, a built-in or Ghostty VT engine, Windows Terminal-compatible settings/actions, self-contained NativeAOT publishing, platform package scripts, and a WebAssembly host. It is a mature infrastructure reference, not a feature checklist that a time-limited Terminal2 port can fully reproduce.

The practical lesson for SIGNALFORGE is to demonstrate a few real end-to-end workflows with reproducible evidence and exact parity limits. Claiming unsupported feature breadth would work against the challenge's migration-completeness and write-up criteria.

## SIGNALFORGE entry checklist

| Requirement | Evidence | Release status |
|---|---|---|
| Existing source application | Terminal2 v1.6.1.4 is pinned at `d3adbd301963b729885cc41fb75b3a80bcdf5472`; source audit is in `MIGRATION.md`. | Eligible as a port of a pre-existing application; attribution and license identity are retained. |
| Compatible open-source license | The project uses GPL-3.0-or-later, compatible with the Terminal2 migration. | Public repository creation and push are in progress. |
| Downloadable builds | Local Windows x64 and Linux x64 archives passed 12-second startup smoke checks (Linux under Ubuntu WSL2). The CI workflow builds, launches and packages all three targets on hosted platform runners, then attaches them to a tagged release. | Await the hosted platform matrix and tagged release before claiming downloadable builds are verified. |
| Before/after screenshots | The README includes genuine Terminal2 and SIGNALFORGE desktop captures; their recorded sizes are 1936×1096 and 1920×1080. The matching screenshots are not identical in resolution. Simulator activity is labeled as simulated. | Included in the public README. |
| Migration write-up | The README summarizes the framework and transport changes, compatibility work, a concrete test finding, untracked engineering time, and known gaps. `MIGRATION.md` and `PARITY.md` hold the detailed audit. | Included in the public README. |
| Entry form | The official form has not been submitted. | Submit after the public repository and release URL are available; confirm any personal contact fields. |

The contest closes at **23:59 UTC on October 23, 2026**. Local Windows and WSL2 results do not stand in for native hosted macOS, Linux, or Windows checks; wait for the platform workflow before stating those checks passed.
