# ARM64 follow-up after S18R

The ninth coordinator ran the follow-up on 24 November 2026 on the rented 16-core
ARM64 machine with .NET SDK 10.0.404 and runtime 10.0.15. All 20 trees built and all 71
timed runs finished. The first launch stopped before the first timed result because
the apphost could not find the installed runtime. The same verified kit then ran with
`DOTNET_ROOT` set to the installation and finished in about 2 h 6 min. The raw results
and both launch logs are kept outside the repository.

The runs rotated the trees each round and used a second build of the base as the
gauge. A gain or fall needed a ratio of medians beyond the gauge band and at least
three paired rounds beyond it on that side.

- **S17R, `following-children`.** The earlier fall for one caller replacing with no
  children did not repeat. Its two repeats gave 0.988 and 0.978, within their
  respective bands of 0.957–1.045 and 0.974–1.027. No rate met the rule for a gain or fall.
- **S18R, `following-children`.** `263851f` after Z4 and `721388a` after Z1 each ran
  against the pre-Z4 tree `085b317` and its gauge. The direct Z1-to-Z4 comparison used
  the same control pair and gave 0.965 to 1.039 across 20 rates. No rate met the rule
  for a gain or fall. The two largest write-rate dips against the pre-Z4 tree each
  went below the band in two of three rounds. Z4 gave 0.908 with 16 callers
  and a child in its own process, against a lower gauge bound of 0.932. The tree with
  Z1 gave 0.926 with 16 callers and an in-process child, against a lower bound of 0.976.
- **K2, `kd`.** `90d7f1a` against `73ec4d1` on 24 `index-writes` rates gave ratios
  from 0.974 to 1.061, with no gain or fall by the rule. The four gains in the earlier
  experiment on the S17R base gave ratios of 1.002 to 1.024 on the applied patch and
  lay within the gauge on this base.
- **K3, `read-by-key-threads`.** The six trees from `3e24ce4` to `6102bbe` ran in five
  rotated rounds against `3e24ce4` and its gauge. No later tree met the rule for a gain
  or fall on the growing-index comparisons. Writer rates at `6102bbe` were 0.979 to
  1.011 of `3e24ce4` across the ten cases and within their bands. The earlier fall on
  the growing insert did not repeat.
- **K4, `full-form-users`.** The median p50 of a replace in the middle form divided by
  the simple scalar form was 1.250 through a class in both builds of `e96650c`. By name
  the ratios were 1.212 and 1.188. The series measured the cost of unique marks and
  the Dictionary region together. Their individual costs remain open for the
  architect's review.
- **Correctness at `73ec4d1`.** All seven fault and waiting-read rows passed. The
  ordering lane passed 20 of 20 runs.
