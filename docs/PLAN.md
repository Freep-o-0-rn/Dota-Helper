# Dota Helper — development plan

## Direction

Dota drafting assistant first, tactical observer later. Never read hidden game memory or automate gameplay.

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
