types
    Db
        customers set of Customer
    Customer
        name text
        email text
        active bool
        orders set of Order
    Order
        item text
        total int
        shipped bool

initialData
    Db 1
        customers: [2, 5]
    Customer 2
        name: "Ada Lovelace"
        email: "ada@example.com"
        active: true
        orders: [3, 4]
    Order 3
        item: "Analytical Engine"
        total: 700
        shipped: false
    Order 4
        item: "Punch cards"
        total: 25
        shipped: true
    Customer 5
        name: "Grace Hopper"
        email: "grace@example.com"
        active: true
