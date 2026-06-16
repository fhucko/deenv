types
    Db
        companyName text
        settings dict of text by text
        customers set of Customer
    Customer
        name text
        email text
        active bool
        orders set of Order
    Order
        date date
        total decimal
        shipped bool
