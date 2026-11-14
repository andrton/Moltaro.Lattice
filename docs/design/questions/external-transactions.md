# External transaction integration

**Accepted for v1:** no participation in external transactions. The
[decision record](../../decisions/external-transactions.md#q09.3-transactions-spanning-lattice-and-application-sql)
owns the contract, alternatives and costs. Internal durable commits are unchanged;
external SQL changes and Lattice writes have no shared atomicity guarantee in v1.

Current work is the [physical engine foundation](../engine-foundation.md).
The first production milestone requires PostgreSQL persistence and uses one database
per instance; physical architecture precedes milestone and sprint planning.
