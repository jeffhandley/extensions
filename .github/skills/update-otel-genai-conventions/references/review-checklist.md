# Review Checklist

Review checklist for gen-ai convention changes. Based on patterns from past PR reviews by domain experts (@stephentoub, @tarekgh, @lmolkova, @CodeBlanch).

## Critical Checks

### 1. Exception Recording Approach
- [ ] Exception events use `ILogger` + `[LoggerMessage]`, NOT `Activity.AddEvent`
- [ ] Log message definitions are in `Common/OpenTelemetryLog.cs`
- [ ] `[LoggerMessage]` message text matches the OTel event name

**Past feedback**: PR #7379 — tarekgh and CodeBlanch directed change from `Activity.AddEvent` to `ILogger`-based approach per OTel migration plan.

### 2. Sensitive Data Gating
- [ ] Attributes that could contain user data are gated behind `EnableSensitiveData`
- [ ] `exception.message` is treated as potentially sensitive
- [ ] Message content serialization respects the sensitive data setting
- [ ] Test coverage for both `EnableSensitiveData = true` and `false`

**Past feedback**: PR #7379 — stephentoub raised whether `exception.message` should be guarded.

### 3. Code Deduplication
- [ ] Cross-cutting telemetry code is shared via `Common/` classes, not duplicated
- [ ] Similar patterns across multiple OpenTelemetry* clients use shared helpers
- [ ] New helper methods are added to `TelemetryHelpers.cs` or `OpenTelemetryLog.cs` as appropriate

**Past feedback**: PR #7379 — tarekgh noted duplicated code across clients and requested consolidation to `Common/`.

### 4. Fluent API Style
- [ ] Activity API calls use fluent chains (`.SetStatus(...).SetTag(...)`)
- [ ] No separate statement for each Activity method call

**Past feedback**: PR #7379 — stephentoub requested fluent chain continuation.

### 5. Test Organization
- [ ] Existing tests augmented with new assertions rather than creating new test methods where possible
- [ ] Both streaming and non-streaming paths tested
- [ ] Sensitive data gating tested (both enabled and disabled)
- [ ] Missing/default value behavior tested

**Past feedback**: PR #7379 — stephentoub asked "do we already have tests validating error.type? If so, can you just augment those".

### 6. Version Reference Completeness
- [ ] All files with a gen-ai semantic conventions version reference use the same version before starting the update
- [ ] ALL OpenTelemetry* client files with a version reference have that reference updated
- [ ] Grep confirms no remaining references to the old version: `grep -rn "v1.OLD" src/Libraries/Microsoft.Extensions.AI/`

### 7. Constants Organization
- [ ] New constants added to appropriate nested class in `OpenTelemetryConsts.cs`
- [ ] Constant names follow PascalCase convention
- [ ] String values match the semantic convention attribute names exactly

### 8. Scope Completeness
- [ ] Changes applied to ALL relevant OpenTelemetry* client classes (not just the chat client)
- [ ] If a change affects embeddings, image generation, speech, etc., those clients are also updated
- [ ] Function invocation changes apply to both `FunctionInvokingChatClient` and shared `Common/FunctionInvocationProcessor.cs`
- [ ] Realtime function invocation via `FunctionInvokingRealtimeClientSession` is also covered if applicable

**Past feedback**: PR #7379 — stephentoub asked to extend changes to additional client types.

### 9. JSON Serialization
- [ ] New content part types have proper inner classes
- [ ] `[JsonSerializable]` registration added to `OtelContext`
- [ ] Switch case added in `SerializeChatMessages()` for new types

### 10. Metric Alignment
- [ ] New metrics have proper instrument creation (Histogram, Counter, etc.)
- [ ] Metric units use constants (`SecondsUnit`, `TokensUnit`)
- [ ] Metric tags align with span attributes where applicable

## Common Mistakes

| Mistake | Correct Approach |
|---------|-----------------|
| Using `Activity.AddEvent` for exceptions | Use `ILogger` + `[LoggerMessage]` |
| Separate Activity API statements | Use fluent chains |
| Creating new test methods for existing scenarios | Augment existing test assertions |
| Only updating `OpenTelemetryChatClient` | Update ALL relevant OpenTelemetry* clients |
| Missing `EnableSensitiveData` gate | Gate any attribute with user-generated content |
| Updating version in one file only | Check for version drift first, then update ALL files with version reference |
| Creating CHANGELOG entries | No CHANGELOGs — info goes in release notes only |
| Using `null` for optional metric units | Use the appropriate unit constant or omit |
