# Blog use cases

All requests use `http://localhost:5080`.

| Use case | Request | Expected result |
|---|---|---|
| Membership card | `/api/memberships/1001` | Membership fields only; `member` and `visits` null |
| Member contact details | `/api/memberships/1001?expand=member` | Member object; `visits` null |
| Recent visits | `/api/memberships/1001?expand=visits` | Two most recent visits; `club` null; `hasMore` true |
| Front-desk screen (nested) | `/api/memberships/1001?expand=member,visits.club&relatedLimit=3` | Member and all three visits with clubs |
| Last visit only | `/api/memberships/1001?expand=visits&relatedLimit=1` | One visit; `hasMore` true |
| Pending renewal | `/api/memberships/1002?expand=visits` | Empty `data` array; `hasMore` false |
| Unsupported relationship | `/api/memberships/1001?expand=payments` | 400 with supported paths |
| Excess depth | `/api/memberships/1001?expand=visits.club.address` | 400 |
| Excess collection size | `/api/memberships/1001?expand=visits&relatedLimit=51` | 400 |
| Unknown membership | `/api/memberships/9999` | 404 |

Every expansion runs as a single SQL query. The baseline selects only the membership row; `member` adds a join; `visits` adds a limited visit subquery; `visits.club` joins that subquery to clubs.
