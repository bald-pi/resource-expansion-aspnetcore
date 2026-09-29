# Blog use cases

All requests use `http://localhost:5080`.

| Use case | Request | Expected result |
|---|---|---|
| Order-status card | `/api/orders/1001` | Order fields only; `customer` and `items` null |
| Customer contact details (single) | `/api/orders/1001?expand=customer` | Customer object; `items` null |
| Item preview | `/api/orders/1001?expand=items` | First two items; `product` null; `hasMore` true |
| Order detail screen (nested) | `/api/orders/1001?expand=customer,items.product&relatedLimit=3` | Customer and all three items with products |
| Smaller preview | `/api/orders/1001?expand=items.product&relatedLimit=1` | One item; `hasMore` true |
| Empty draft | `/api/orders/1002?expand=items` | Empty `data` array; `hasMore` false |
| Unsupported relationship | `/api/orders/1001?expand=payments` | 400 with supported paths |
| Excess depth | `/api/orders/1001?expand=items.product.supplier` | 400 |
| Excess collection size | `/api/orders/1001?expand=items&relatedLimit=51` | 400 |
| Unknown order | `/api/orders/9999` | 404 |

Every expansion runs as a single SQL query. The baseline selects only the order row; `customer` adds a join; `items.product` adds a limited item subquery joined to products.
