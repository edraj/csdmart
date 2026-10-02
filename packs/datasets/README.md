# Datasets

Demo content for the packs, kept separate from the pack structures so a pack's
skeleton can be installed without its data and so the generated scales never
land in a commit.

```
datasets/shanidar/<scale>/<pack>/      merged into that pack's space at build
```

A directory under a scale is laid out exactly like the pack's `space/` tree —
`build.sh` copies it over the top, so a dataset adds entries to folders the
pack structure already declares. A scale that does not exist is skipped
silently, which is what makes `--scale medium` work before anything has
generated it.

`small` is committed. `medium` (~×10) and `large` (~×250) are generated on
demand and gitignored — see the "Scales" decision in [../PLAN.md](../PLAN.md).

Nothing here is real. No real company, person, brand or subscriber: emails are
`@example.com`, MSISDNs follow the synthetic `+964 7X0 000 NNNN` pattern, and
the operator, its sites and its staff are invented.
