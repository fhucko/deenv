types
    Db
        customers: set of Customer
    Customer
        name: text
        email: text
        active: bool
        orders: set of Order
    Order
        item: text
        total: int
        shipped: bool

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

ui
    view Customer(customer)
        return <main class="customer-card">
            <h2 class="customer-name">
                customer.name
            <input class="email" value={customer.email}>
            <label>
                <input type="checkbox" class="active" checked={customer.active}>
                "Active"
            <h3>
                "Open orders"
            foreach o in customer.orders.where(x => x.shipped == false)
                <div class="open-order">
                    o.item

    view "/dashboard"(path)
        return <main class="dashboard">
            <h1>
                "Dashboard"
            foreach c in db.customers.where(x => x.active == true)
                <div class="active-customer">
                    c.name
