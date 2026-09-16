# FORTIQ design QA

- Source visual truth: `C:/Users/cybou/AppData/Local/Temp/codex-clipboard-af89cde2-ee40-4c5c-ab8a-fa26c254b0cf.png`
- Implementation screenshots: `audit/02-home-desktop.png`, `audit/03-home-mobile.png`, `audit/04-contact-desktop.png`
- Combined comparison: `audit/comparison-home.png`
- Viewports: 1600 × 1000 desktop, 905 × 846 compact/tablet, 390 × 844 mobile
- Source pixels: 2236 × 1716. Desktop implementation pixels: 1600 × 1000 at device scale factor 1. Mobile implementation pixels: 390 × 844 at device scale factor 1.
- Normalization: the source and compact implementation were scaled to equal 896 px columns in `audit/comparison-home.png`; browser chrome in the source was excluded from fidelity judgments.
- State: homepage at initial state; compact navigation also checked open and closed; contact page initial state; FAQ first item toggled.

## Full-view comparison evidence

The implementation preserves the source's dark sovereign-tech identity, cyan/blue palette, particle mesh, centered hero, two primary actions, and four trust statistics. The revised hierarchy is intentionally clearer: stronger contrast, balanced hero wrapping, tighter vertical rhythm, and a controlled responsive navigation breakpoint.

## Focused checks

- Typography: Inter and JetBrains Mono remain intact; display weight, line height, wrapping, and small-text contrast were checked at desktop and mobile sizes.
- Spacing: header, hero, CTA, trust-stat, card, and mobile gutters were checked. No horizontal overflow remains at 390 px or on the contact page at 1400 px.
- Colors: brand cyan/blue remains consistent; secondary and muted foreground tokens were raised for readability.
- Assets: the supplied FORTIQ logo and existing icon font are retained; no logo or visible source asset was approximated.
- Copy: service, pricing, trust, and CTA copy remains unchanged except for the requested contact address.
- Focused regions: header/navigation, hero CTA group, trust statistics, mobile menu, contact address, contact form, and FAQ accordion were inspected separately because those controls carry the main conversion flow.

## Findings and iteration history

1. P1 — desktop/tablet header overflowed before the mobile breakpoint. Fixed by introducing a 1480 px compact-navigation breakpoint and retested at 905, 1400, and 1600 px.
2. P1 — contact form displayed a fake success state without sending anywhere. Fixed by generating a prefilled `mailto:info@fortiq.com` request and keeping native required-field validation.
3. P2 — mobile menu lacked state semantics and close behavior. Fixed with `aria-expanded`, `aria-controls`, Escape-to-close, animated open/close state, scroll locking, and readable link styling.
4. P2 — muted copy and card boundaries were too low-contrast. Fixed through revised foreground and surface tokens, then visually rechecked.
5. P2 — decorative content caused document-width overflow. Fixed with root-level horizontal clipping; mobile and contact pages report no overflow.

Post-fix evidence: `audit/02-home-desktop.png`, `audit/03-home-mobile.png`, and `audit/04-contact-desktop.png`. Browser console contained no warnings or errors during the final FAQ interaction check.

## Residual P3 polish

- The particle positions are randomized, so screenshots will never be pixel-identical between reloads; this is expected and does not affect layout or usability.
- A true server-side form endpoint could replace the mail-client handoff later if the site gains backend hosting.

final result: passed
