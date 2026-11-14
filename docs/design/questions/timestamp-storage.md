# Q03.4.1: Temporal types and timestamp representation

**Accepted for v1 on 13 November 2026:** option A for instants, plus explicit support
for DateOnly, TimeOnly and TimeSpan. All four types use numeric value storage.

| .NET type | Arena representation | Value payload |
| --- | --- | --- |
| DateOnly | Int32 day number | 4 bytes |
| TimeOnly | Int64 ticks since midnight | 8 bytes |
| TimeSpan | Signed Int64 duration ticks | 8 bytes |
| DateTimeOffset | Int64 UTC ticks; reads have zero offset | 8 bytes |

The [decision record](../../decisions/schema-and-types.md#utc-timestamps) owns UTC
normalization and its alternative. [Date/time/duration contracts](../../decisions/schema-and-types.md#dateonly-timeonly-and-timespan)
keep date-only, wall-clock time and signed duration distinct. Tick resolution is
100 nanoseconds. DateTimeOffset's original offset is not retained; DateOnly/TimeOnly
are not converted to UTC. Values are typed in the schema despite numeric backing.

Sizes exclude alignment and nullable/index metadata. No temporal primary keys,
named-timezone engine or recurring scheduling feature is introduced. Q03.4.1 is now
fully accepted, including the later Binary and numeric extensions;
numerical limits remain Q14. No runtime implementation or benchmark was performed.
The current proposal is [external transaction integration](external-transactions.md).
