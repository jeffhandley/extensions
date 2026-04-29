# Implementation Procedure

Used by Modes 2 (Autopilot), 4 (CCA Implementation), and 5 (Plan-then-Implement) when actually applying convention changes.

1. Read [implementation-patterns.md](implementation-patterns.md) and [testing-guide.md](testing-guide.md)
2. Read [review-checklist.md](review-checklist.md) to anticipate review feedback
3. Apply changes in this order:
   - Add new constants to `OpenTelemetryConsts.cs`
   - Add attribute/metric emission to the relevant OpenTelemetry* client classes
   - Update version references in doc comments across all files that reference the convention version
   - Update or augment tests
4. Self-review against [review-checklist.md](review-checklist.md)
5. Validate per the **Validation** section in `SKILL.md`
