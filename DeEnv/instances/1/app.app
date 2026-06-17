types
    Db
        designs set of Design
    Design
        label text
        initialData text
        common text
        ui text
        types set of MetaType
    MetaType
        name text
        baseType text
        values text
        order int
        props set of MetaProp
    MetaProp
        name text
        type text
        cardinality text
        keyType text
        order int

initialData
    MetaProp 2
        name: "users"
        type: "User"
        order: 10
        cardinality: "set"
    MetaProp 4
        name: "name"
        type: "text"
        order: 10
    MetaProp 5
        name: "todoLists"
        type: "TodoList"
        order: 20
        cardinality: "set"
    MetaProp 7
        name: "name"
        type: "text"
        order: 10
    MetaProp 8
        name: "items"
        type: "TodoItem"
        order: 20
        cardinality: "set"
    MetaProp 10
        name: "text"
        type: "text"
        order: 10
    MetaProp 11
        name: "checked"
        type: "bool"
        order: 20
    MetaProp 14
        name: "companyName"
        type: "text"
        order: 10
    MetaProp 15
        name: "settings"
        type: "text"
        order: 20
        cardinality: "dictionary"
        keyType: "text"
    MetaProp 16
        name: "customers"
        type: "Customer"
        order: 30
        cardinality: "set"
    MetaProp 18
        name: "name"
        type: "text"
        order: 10
    MetaProp 19
        name: "email"
        type: "text"
        order: 20
    MetaProp 20
        name: "active"
        type: "bool"
        order: 30
    MetaProp 21
        name: "orders"
        type: "Order"
        order: 40
        cardinality: "set"
    MetaProp 23
        name: "date"
        type: "date"
        order: 10
    MetaProp 24
        name: "total"
        type: "decimal"
        order: 20
    MetaProp 25
        name: "shipped"
        type: "bool"
        order: 30
    MetaProp 28
        name: "customers"
        type: "Customer"
        order: 10
        cardinality: "set"
    MetaProp 30
        name: "name"
        type: "text"
        order: 10
    MetaProp 31
        name: "email"
        type: "text"
        order: 20
    MetaProp 32
        name: "active"
        type: "bool"
        order: 30
    MetaProp 33
        name: "orders"
        type: "Order"
        order: 40
        cardinality: "set"
    MetaProp 35
        name: "item"
        type: "text"
        order: 10
    MetaProp 36
        name: "total"
        type: "int"
        order: 20
    MetaProp 37
        name: "shipped"
        type: "bool"
        order: 30
    MetaType 3
        name: "Db"
        baseType: "object"
        order: 10
        props: [2]
    MetaType 6
        name: "User"
        baseType: "object"
        order: 20
        props: [4, 5]
    MetaType 9
        name: "TodoList"
        baseType: "object"
        order: 30
        props: [7, 8]
    MetaType 12
        name: "TodoItem"
        baseType: "object"
        order: 40
        props: [10, 11]
    MetaType 17
        name: "Db"
        baseType: "object"
        order: 10
        props: [14, 15, 16]
    MetaType 22
        name: "Customer"
        baseType: "object"
        order: 20
        props: [18, 19, 20, 21]
    MetaType 26
        name: "Order"
        baseType: "object"
        order: 30
        props: [23, 24, 25]
    MetaType 29
        name: "Db"
        baseType: "object"
        order: 10
        props: [28]
    MetaType 34
        name: "Customer"
        baseType: "object"
        order: 20
        props: [30, 31, 32, 33]
    MetaType 38
        name: "Order"
        baseType: "object"
        order: 30
        props: [35, 36, 37]
    Design 13
        label: "instance"
        initialData: "initialData\n    Db 1\n        users: [2]\n    User 2\n        name: \"User 1\"\n        todoLists: [3]\n    TodoList 3\n        name: \"List 1\"\n        items: []\n"
        common: ""
        ui: "ui\n    var title = \"Todo list\"\n    var selectedUser\n    var selectedList\n    var newUser = getNewUser()\n    var newList = getNewList()\n    var newItem = getNewItem()\n\n    fn selectUser(user)\n        selectedUser = user\n        selectedList = null\n\n    fn getNewUser()\n        return { name: \"\", todoLists: [] }\n\n    fn addNewUser()\n        db.users.add(newUser)\n        newUser = getNewUser()\n\n    fn getNewList()\n        return { name: \"\", items: [] }\n\n    fn addNewList()\n        selectedUser.todoLists.add(newList)\n        newList = getNewList()\n\n    fn getNewItem()\n        return { text: \"\", checked: false }\n\n    fn addNewItem()\n        selectedList.items.add(newItem)\n        newItem = getNewItem()\n\n    fn aboutPage()\n        return <p class=\"about\">\n            \"This is the about page\"\n\n    fn usersPage()\n        return <main class=\"users\">\n            <h2>\n                \"Users\"\n            <input class=\"new-user\" value={newUser.name}>\n            <button class=\"add-user\" onClick={addNewUser}>\n                \"Add user\"\n            foreach user in db.users\n                <div class=\"user-row\">\n                    <span class=\"user-name\" onClick={() => selectUser(user)}>\n                        user.name\n                    <button class=\"remove-user\" onClick={() => db.users.remove(user)}>\n                        \"Remove\"\n            if selectedUser != null\n                <h2 class=\"selected-user\">\n                    \"User \"\n                    selectedUser.name\n                <input class=\"new-list\" value={newList.name}>\n                <button class=\"add-list\" onClick={addNewList}>\n                    \"Add list\"\n                foreach list in selectedUser.todoLists\n                    <div class=\"list-row\">\n                        <span class=\"list-name\" onClick={() => selectedList = list}>\n                            list.name\n                        <button class=\"remove-list\" onClick={() => selectedUser.todoLists.remove(list)}>\n                            \"Remove\"\n                if selectedList != null\n                    <h2 class=\"selected-list\">\n                        \"List \"\n                        selectedList.name\n                    <input class=\"new-item\" value={newItem.text}>\n                    <button class=\"add-item\" onClick={addNewItem}>\n                        \"Add item\"\n                    foreach item in selectedList.items\n                        <div class=\"item-row\">\n                            <input type=\"checkbox\" class=\"item-check\" checked={item.checked}>\n                            if item.checked\n                                <span class=\"item-done\">\n                                    item.text\n                            else\n                                <input class=\"item-text\" value={item.text}>\n                            <button class=\"remove-item\" onClick={() => selectedList.items.remove(item)}>\n                                \"Remove\"\n\n    fn render()\n        var page\n        if path == \"/about\"\n            title = \"About\"\n            page = aboutPage\n        else\n            title = \"Todo list\"\n            page = usersPage\n        return <div>\n            <h1>\n                \"Todo list\"\n            <nav>\n                <button class=\"nav-users\" onClick={() => path = \"/\"}>\n                    \"Users\"\n                <button class=\"nav-about\" onClick={() => path = \"/about\"}>\n                    \"About\"\n            page()\n"
        types: [3, 6, 9, 12]
    Design 27
        label: "crm"
        initialData: ""
        common: ""
        ui: ""
        types: [17, 22, 26]
    Design 39
        label: "shop"
        initialData: "initialData\n    Db 1\n        customers: [2, 5]\n    Customer 2\n        name: \"Ada Lovelace\"\n        email: \"ada@example.com\"\n        active: true\n        orders: [3, 4]\n    Order 3\n        item: \"Analytical Engine\"\n        total: 700\n        shipped: false\n    Order 4\n        item: \"Punch cards\"\n        total: 25\n        shipped: true\n    Customer 5\n        name: \"Grace Hopper\"\n        email: \"grace@example.com\"\n        active: true\n"
        common: ""
        ui: ""
        types: [29, 34, 38]
    Db 1
        designs: [13, 27, 39]

ui
    var newAppPort = 9100
    var newInfraPort = 9101
    var newLabel = ""
    var newDesignId = 0
    var newInstanceName = ""
    var renameId = 0
    var renameName = ""

    fn addType(design)
        design.types.add({ name: "", baseType: "object", values: "", order: 0, props: [] })

    fn addProp(type)
        type.props.add({ name: "", type: "text", cardinality: "", keyType: "", order: 0 })

    fn addDesign()
        db.designs.add({ label: newLabel, types: [], initialData: "" })
        newLabel = ""

    fn startRename(i)
        renameId = i.id
        renameName = i.app

    fn doRename(i)
        sys.rename(i.id, renameName)
        renameId = 0

    fn nav()
        return <nav class="ide-nav">
            <a class="nav-instances" href="/instances">
                "Instances"
            <a class="nav-designs" href="/designs">
                "Designs"

    fn designsListPage()
        return <main class="ide-designs">
            <h1>
                "Designs"
            <div class="new-design">
                <input class="new-design-label" value={newLabel}>
                <button class="add-design" onClick={() => addDesign()}>
                    "Add"
            foreach d in db.designs
                <div class="design-row">
                    <span class="design-label">
                        d.label
                    <a class="edit-design" href={sys.nest("/designs", sys.id(d))}>
                        "Edit"
                    <button class="delete-design" onClick={() => db.designs.remove(d)}>
                        "Delete"

    fn designEditor(design)
        return <section class="design-editor">
            <h2 class="design-label">
                design.label
            <button class="add-type" onClick={() => addType(design)}>
                "Add type"
            foreach type in design.types
                <div class="type-row">
                    <input class="type-name" value={type.name}>
                    <input class="type-base" value={type.baseType}>
                    <input class="type-values" value={type.values}>
                    <button class="remove-type" onClick={() => design.types.remove(type)}>
                        "Remove type"
                    <button class="add-prop" onClick={() => addProp(type)}>
                        "Add prop"
                    foreach prop in type.props
                        <div class="prop-row">
                            <input class="prop-name" value={prop.name}>
                            <input class="prop-type" value={prop.type}>
                            <select class="prop-cardinality" value={prop.cardinality}>
                                <option value="">
                                    "single"
                                <option value="set">
                                    "set"
                                <option value="dictionary">
                                    "dictionary"
                            <input class="prop-keytype" value={prop.keyType}>
                            <button class="remove-prop" onClick={() => type.props.remove(prop)}>
                                "Remove prop"
            <label class="ui-label">
                "UI"
            <textarea class="design-ui" value={sys.field(design, "ui")}>
            <label class="common-label">
                "Common"
            <textarea class="design-common" value={sys.field(design, "common")}>
            <label class="initial-label">
                "Initial data"
            <textarea class="design-initial" value={sys.field(design, "initialData")}>

    fn designEditorPage()
        var routeId = sys.toInt(sys.segment(path, 2))
        return <main class="ide-design-edit">
            <h1>
                "Edit design"
            <a class="back" href="/designs">
                "Back"
            foreach d in db.designs
                if sys.id(d) == routeId
                    designEditor(d)

    fn instancesListPage()
        return <main class="ide-list">
            <h1>
                "Instances"
            <div class="new-instance">
                <select class="new-instance-design" value={newDesignId}>
                    foreach d in db.designs
                        <option value={sys.id(d)}>
                            d.label
                <input class="new-instance-name" value={newInstanceName}>
                <input class="new-instance-app-port" value={newAppPort}>
                <input class="new-instance-infra-port" value={newInfraPort}>
                foreach d in db.designs
                    if sys.id(d) == newDesignId
                        <button class="create-instance" onClick={() => sys.create(d, newInstanceName, newAppPort, newInfraPort)}>
                            "Create"
            foreach i in sys.instances
                <div class="instance-row">
                    if renameId == i.id
                        <input class="rename-input" value={renameName}>
                        <button class="rename-save" onClick={() => doRename(i)}>
                            "Save"
                        <button class="rename-cancel" onClick={() => renameId = 0}>
                            "Cancel"
                    else
                        <span class="instance-app">
                            i.app
                        <button class="rename-instance" onClick={() => startRename(i)}>
                            "Rename"
                    <span class="instance-port">
                        i.port
                    foreach d in db.designs
                        if sys.id(d) == i.designId
                            <span class="design-label">
                                d.label
                    <a class="open-instance" href={sys.nest("/instances", i.id)}>
                        "Open"
                    <button class="clone-instance" onClick={() => sys.cloneInstance(i.id, newAppPort, newInfraPort)}>
                        "Clone"
                    <button class="delete-instance" onClick={() => sys.delete(i.id)}>
                        "Delete"

    fn designSelector(instanceId, currentDesignId)
        var state = { pick: currentDesignId }
        fn render()
            return <div class="design-selector">
                <select class="design-pick" value={state.pick}>
                    foreach d in db.designs
                        <option value={sys.id(d)}>
                            d.label
                foreach d in db.designs
                    if sys.id(d) == state.pick
                        <button class="apply-design" onClick={() => sys.setDesign(d, instanceId)}>
                            "Apply"
        return render

    fn instanceSelectorPage()
        var routeId = sys.toInt(sys.segment(path, 2))
        return <main class="ide-instance">
            <h1>
                "Instance"
            <a class="back" href="/instances">
                "Back"
            foreach i in sys.instances
                if i.id == routeId
                    <span class="instance-app">
                        i.app
                    designSelector(i.id, i.designId)()
                    <button class="clone-instance" onClick={() => sys.cloneInstance(i.id, newAppPort, newInfraPort)}>
                        "Clone"
                    <button class="delete-instance" onClick={() => sys.delete(i.id)}>
                        "Delete"

    fn render()
        var page
        var section = sys.segment(path, 1)
        var sub = sys.segment(path, 2)
        if section == "designs"
            if sub == ""
                page = designsListPage
            else
                page = designEditorPage
        else
            if sub == ""
                page = instancesListPage
            else
                page = instanceSelectorPage
        return <div class="ide">
            nav()
            page()
