# Dota Helper — development plan

## Direction

Dota drafting assistant first, tactical observer later. Never read hidden game memory or automate gameplay.

## 0.4.1 — portrait cards, roster freshness, Titan (implemented)

- Compact recommendation rows with original hero portraits fetched from official Steam CDN (OpenDota image metadata preferred).
- The full score explanation is now available in a separate expandable detail area; no details are lost.
- Valve official /datafeed/herolist supplements OpenDota /heroes; catalog TTL 24 hours, so missing newest hero definitions do not silently remove playable heroes.
- Largo support and Kez carry recognized by provisional position profiles.
- Immortal (Titan) is displayed as a discrete rank and uses only its own 8_pick/8_win if available; missing data remains missing.
- Steam vanity (/id/) resolution is a planned follow-up, not silently supported.

## 0.4 — full enemy team priority (implemented)

- Enemy role in each slot is manually selected (0 unknown; 1 carry; 2 mid; 3 offlane; 4/5 support).
- Every selected enemy matchup contributes to counterpick scoring. Base weights: cores 1.7, supports 1.0, unknowns 1.2; lane opponents multiply weight by 1.25.
- Missing matchup data is neutral; do not imply an observed lane outcome. Show per-enemy score contributions and clear source limitations.
- Pool UI has exactly two modes: standard by role (no popularity cutoff), experimental (all heroes).
- OpenDota public account lookup suggests a rank tier; manual override remains, and Immortal lacks a dedicated bracket in the current heroStats UI.
- Professional baseline uses pro_pick/pro_win, never a silent fallback to the ranked aggregate. Counterpick data is still league-based and shared between modes.
- Test weights, meta separation and missing-data behavior before further algorithm tuning.

## 0.3 — all five positions (implemented)

- Position 1: carry, position 2: mid, position 3: offlane, position 4: soft support, position 5: hard support.
- Every standard-pool mode enforces a conservative role-specific whitelist for ALL FIVE positions.
- Off-role favorites cannot override the role constraint. Only explicit Experimental mode relaxes it.
- Wraith King is eligible as carry but not offlane/mid/support in this baseline; Shadow Fiend as carry/mid; Axe as offlane; Rubick and Crystal Maiden as support. Nonstandard choices are possible in real Dota, which is why Experimental exists.
- Regression tests explicitly verify roles 1–5. No position-specific empirical patch winrates are claimed.
- Priority: replace provisional hand-curated pools with STRATZ or another verified, dated, position-specific data feed plus sample confidence.

## 0.2.1 — support position filter (implemented)

- Standard pos 4 and 5 pools are now conservative, explicit, separately maintained lists by internal hero slug.
- Wraith King, Lifestealer and other heroes outside the support pool are filtered out even with high global winrate or an OpenDota "Support"/"Disabler" tag.
- Experimental mode permits any hero but visibly warns when outside the standard position pool.
- Favorites cannot silently bypass this suitability gate.
- Overall win and pick rate labels explicitly say **all positions**; these stats do not imply performance for position 4 or 5.
- Regression tests cover Wraith King, Lifestealer, Rubick, Lich, favorites and experimental picks.
- Next step: replace static support lists with independently verified, patch/rank-filtered observed position statistics. Do not interpret the lists as meta rankings.

## 0.2 — transparent ranking (implemented)

- Manual 4 ally / 5 enemy draft and selected rank position 1–5.
- OpenDota /heroStats aggregate pick and win counts for available rank brackets.
- Conservative matchup score with sample shrinkage and missing-opponent neutrality.
- Favorite heroes and alternative hero pools: common, all, favorites-only.
- Default common pool excludes the bottom quarter by pick count in the selected bracket. This is a convenience filter, **not a skill or balance verdict**.
- Explain the source and limits of every displayed score.
- Automated build and ranking smoke tests on Windows.

## Important limitations

- /heroStats and /heroes/{id}/matchups are NOT guaranteed to be restricted to the current patch.
- Matchup statistics do NOT constitute lane-specific results.
- Rank groups are applied to overall pick/win stats, NOT to matchup results.
- Immortal statistics can be withheld/zero from OpenDota. Only available Herald through Divine groups are exposed.
- "Role" uses Valve/OpenDota broad labels, not observed position 1–5 distributions.
- Ally synergy is not yet calculated. No teamfight composition strategy, pick/ban, or flexible assignments yet.
- No claim that the numerical 0–100 heuristic is a win probability.
- Pick rate is an approximation based on total 10 hero picks per match and may be distorted by the sampled dataset; compare within the same bracket only.

## Next steps

1. Verify real data under several drafts; inspect Tree Protector results across ranks and filters.
2. Integrate a reliable role-by-position dataset with date/patch/sample metadata.
3. Add synergy evidence and lane outcomes with confidence intervals.
4. Replace plain dropdowns with portrait-based draft UI and search.
5. Game State Integration feasibility test for drafting, compliant with Valve policies.
6. Tactical assistant: visible-information-only objective timer, suggested wards and danger zones.

## Local development

- Build: dotnet build src/DotaHelper/DotaHelper.csproj
- Run: dotnet run --project src/DotaHelper/DotaHelper.csproj
- Automated test: dotnet run --project tests/DotaHelper.SmokeTests/DotaHelper.SmokeTests.csproj
- Downloads: successful GitHub Actions artifacts from the Windows build workflow.
