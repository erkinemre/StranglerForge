# Catalog extraction impact analysis

Current source of truth is LegacyCommerce SQL Server Products. Product contains Id (GUID), Name, and Price. The seed product has Id 00000000-0000-0000-0000-000000000201. Public legacy catalog contracts are POST /api/products and GET /api/products. There is no product update or detail endpoint today; the extracted service adds detail lookup for internal use while preserving existing public contracts.

Direct dependencies: CreateProductHandler writes SQL Products; CommerceQueries.GetProductsAsync reads them. CreateOrderHandler reads Product.Price and Product.Id before reserving inventory. AddStockHandler checks Product existence. CommerceDb maps and seeds Products. Existing SQL migrations, tests, and gateway catalog route reference these paths.

Indirect dependencies: CommerceOrder.ProductId and StockItem.ProductId are GUID references without cross-database foreign keys. Historical orders snapshot UnitPrice. Payments and notifications depend on order data, not current catalog data. Customer has no product dependency.

Extraction changes the authoritative writer to PostgreSQL Catalog Service. The legacy Product table remains temporarily for audit and rollback but is not queried by extracted-mode order or inventory workflows. A narrow HTTP anti-corruption adapter converts the Catalog response into ProductSnapshot for those workflows. Catalog availability now affects new orders and stock additions; existing orders and payments remain available. Failure maps to a service-unavailable response instead of silently falling back to stale SQL data.
