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
    MetaProp 40
        name: "designs"
        type: "Design"
        order: 10
        cardinality: "set"
    MetaProp 42
        name: "label"
        type: "text"
        order: 10
    MetaProp 43
        name: "initialData"
        type: "text"
        order: 20
    MetaProp 44
        name: "common"
        type: "text"
        order: 30
    MetaProp 45
        name: "ui"
        type: "text"
        order: 40
    MetaProp 46
        name: "types"
        type: "MetaType"
        order: 50
        cardinality: "set"
    MetaProp 48
        name: "name"
        type: "text"
        order: 10
    MetaProp 49
        name: "baseType"
        type: "text"
        order: 20
    MetaProp 50
        name: "values"
        type: "text"
        order: 30
    MetaProp 51
        name: "order"
        type: "int"
        order: 40
    MetaProp 52
        name: "props"
        type: "MetaProp"
        order: 50
        cardinality: "set"
    MetaProp 54
        name: "name"
        type: "text"
        order: 10
    MetaProp 55
        name: "type"
        type: "text"
        order: 20
    MetaProp 56
        name: "cardinality"
        type: "text"
        order: 30
    MetaProp 57
        name: "keyType"
        type: "text"
        order: 40
    MetaProp 58
        name: "order"
        type: "int"
        order: 50
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
    MetaType 41
        name: "Db"
        baseType: "object"
        order: 10
        props: [40]
    MetaType 47
        name: "Design"
        baseType: "object"
        order: 20
        props: [42, 43, 44, 45, 46]
    MetaType 53
        name: "MetaType"
        baseType: "object"
        order: 30
        props: [48, 49, 50, 51, 52]
    MetaType 59
        name: "MetaProp"
        baseType: "object"
        order: 40
        props: [54, 55, 56, 57, 58]
    Design 13
        label: "todo"
        initialData: "initialData\n    Db 1\n        users: [2]\n    User 2\n        name: \"User 1\"\n        todoLists: [3]\n    TodoList 3\n        name: \"List 1\"\n        items: []\n"
        common: ""
        ui: "ui\n    var selectedUser\n    var newUser = getNewUser()\n    var newList = getNewList()\n\n    fn getNewUser()\n        return { name: \"\", todoLists: [] }\n\n    fn getNewList()\n        return { name: \"\", items: [] }\n\n    fn getNewItem()\n        return { text: \"\", checked: false }\n\n    fn addNewUser()\n        db.users.add(newUser)\n        selectedUser = newUser\n        newUser = getNewUser()\n\n    fn addNewList()\n        selectedUser.todoLists.add(newList)\n        newList = getNewList()\n\n    fn chipClass(user)\n        if selectedUser == null\n            return \"user-chip\"\n        if sys.id(user) == sys.id(selectedUser)\n            return \"user-chip selected\"\n        return \"user-chip\"\n\n    fn userSelector()\n        return <section class=\"user-bar\">\n            foreach user in db.users\n                <button class={chipClass(user)} onClick={() => selectedUser = user}>\n                    user.name\n            <input class=\"new-user\" value={newUser.name}>\n            <button class=\"add-user\" onClick={addNewUser}>\n                \"Add user\"\n\n    fn itemAdder(list)\n        var draft = { item: getNewItem() }\n        fn add()\n            list.items.add(draft.item)\n            draft.item = getNewItem()\n        fn view()\n            return <div class=\"add-item\">\n                <input class=\"new-item\" value={draft.item.text}>\n                <button class=\"add-item-btn\" onClick={add}>\n                    \"Add\"\n        return view\n\n    fn listCard(list)\n        return <article class=\"todo-card\">\n            <h3 class=\"list-name\">\n                list.name\n            <ul class=\"checklist\">\n                foreach item in list.items\n                    <li class=\"item-row\">\n                        <Input obj={item} desc={sys.schema(\"TodoItem\", \"checked\")}>\n                        <Input obj={item} desc={sys.schema(\"TodoItem\", \"text\")} variant=\"standard\">\n                        <button class=\"remove-item\" onClick={() => list.items.remove(item)}>\n                            \"×\"\n            <itemAdder list={list}>\n\n    fn render()\n        return <main class=\"todo-app\">\n            <h1>\n                \"Todos\"\n            userSelector()\n            if selectedUser != null\n                <section class=\"user-lists\">\n                    <h2 class=\"selected-user\">\n                        selectedUser.name\n                    <div class=\"add-list\">\n                        <input class=\"new-list\" value={newList.name}>\n                        <button class=\"add-list-btn\" onClick={addNewList}>\n                            \"Add list\"\n                    <div class=\"cards\">\n                        foreach list in selectedUser.todoLists\n                            listCard(list)\n"
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
    Design 60
        label: "designer"
        initialData: ""
        common: ""
        ui: "ui\n    var newAppPort = 9100\n    var newInfraPort = 9101\n    var newLabel = \"\"\n    var newDesignId = 0\n    var newInstanceName = \"\"\n    var renameId = 0\n    var renameName = \"\"\n\n    fn addType(design)\n        design.types.add({ name: \"\", baseType: \"object\", values: \"\", order: 0, props: [] })\n\n    fn addProp(type)\n        type.props.add({ name: \"\", type: \"text\", cardinality: \"\", keyType: \"\", order: 0 })\n\n    fn addDesign()\n        db.designs.add({ label: newLabel, types: [], initialData: \"\" })\n        newLabel = \"\"\n\n    fn startRename(i)\n        renameId = i.id\n        renameName = i.app\n\n    fn doRename(i)\n        sys.rename(i.id, renameName)\n        renameId = 0\n\n    fn navBar()\n        return <nav class=\"ide-nav\">\n            <a class=\"nav-instances\" href=\"/instances\">\n                \"Instances\"\n            <a class=\"nav-designs\" href=\"/designs\">\n                \"Designs\"\n\n    fn designsListPage()\n        return <main class=\"ide-designs\">\n            <h1>\n                \"Designs\"\n            <div class=\"new-design\">\n                <input class=\"new-design-label\" value={newLabel}>\n                <button class=\"add-design\" onClick={() => addDesign()}>\n                    \"Add\"\n            <table class=\"designs-table\">\n                <tr class=\"designs-head\">\n                    <th>\n                        \"Design\"\n                    <th>\n                        \"Actions\"\n                foreach d in db.designs\n                    <tr class=\"design-row\">\n                        <td class=\"design-label\">\n                            d.label\n                        <td>\n                            <a class=\"edit-design\" href={sys.nest(\"/designs\", sys.id(d))}>\n                                \"Edit\"\n                            <button class=\"delete-design\" onClick={() => db.designs.remove(d)}>\n                                \"Delete\"\n\n    fn designEditor(design)\n        return <section class=\"design-editor\">\n            <h2 class=\"design-label\">\n                design.label\n            <button class=\"add-type\" onClick={() => addType(design)}>\n                \"Add type\"\n            foreach type in design.types\n                <div class=\"type-row\">\n                    <input class=\"type-name\" value={type.name}>\n                    <input class=\"type-base\" value={type.baseType}>\n                    <input class=\"type-values\" value={type.values}>\n                    <button class=\"remove-type\" onClick={() => design.types.remove(type)}>\n                        \"Remove type\"\n                    <button class=\"add-prop\" onClick={() => addProp(type)}>\n                        \"Add prop\"\n                    foreach prop in type.props\n                        <div class=\"prop-row\">\n                            <input class=\"prop-name\" value={prop.name}>\n                            <input class=\"prop-type\" value={prop.type}>\n                            <select class=\"prop-cardinality\" value={prop.cardinality}>\n                                <option value=\"\">\n                                    \"single\"\n                                <option value=\"set\">\n                                    \"set\"\n                                <option value=\"dictionary\">\n                                    \"dictionary\"\n                            <input class=\"prop-keytype\" value={prop.keyType}>\n                            <button class=\"remove-prop\" onClick={() => type.props.remove(prop)}>\n                                \"Remove prop\"\n            <label class=\"ui-label\">\n                \"UI\"\n            <textarea class=\"design-ui\" value={sys.field(design, \"ui\")}>\n            <label class=\"common-label\">\n                \"Common\"\n            <textarea class=\"design-common\" value={sys.field(design, \"common\")}>\n            <label class=\"initial-label\">\n                \"Initial data\"\n            <textarea class=\"design-initial\" value={sys.field(design, \"initialData\")}>\n\n    fn designEditorPage()\n        var routeId = sys.toInt(sys.segment(path, 2))\n        return <main class=\"ide-design-edit\">\n            <h1>\n                \"Edit design\"\n            <a class=\"back\" href=\"/designs\">\n                \"Back\"\n            foreach d in db.designs\n                if sys.id(d) == routeId\n                    designEditor(d)\n\n    fn instancesListPage()\n        return <main class=\"ide-list\">\n            <h1>\n                \"Instances\"\n            <div class=\"new-instance\">\n                <select class=\"new-instance-design\" value={newDesignId}>\n                    foreach d in db.designs\n                        <option value={sys.id(d)}>\n                            d.label\n                <input class=\"new-instance-name\" value={newInstanceName}>\n                <input class=\"new-instance-app-port\" value={newAppPort}>\n                <input class=\"new-instance-infra-port\" value={newInfraPort}>\n                foreach d in db.designs\n                    if sys.id(d) == newDesignId\n                        <button class=\"create-instance\" onClick={() => sys.create(d, newInstanceName, newAppPort, newInfraPort)}>\n                            \"Create\"\n            <table class=\"instances-table\">\n                <tr class=\"instances-head\">\n                    <th>\n                        \"Instance\"\n                    <th>\n                        \"Port\"\n                    <th>\n                        \"Design\"\n                    <th>\n                        \"Actions\"\n                foreach i in sys.instances\n                    <tr class=\"instance-row\">\n                        <td>\n                            if renameId == i.id\n                                <input class=\"rename-input\" value={renameName}>\n                                <button class=\"rename-save\" onClick={() => doRename(i)}>\n                                    \"Save\"\n                                <button class=\"rename-cancel\" onClick={() => renameId = 0}>\n                                    \"Cancel\"\n                            else\n                                <span class=\"instance-app\">\n                                    i.app\n                                <button class=\"rename-instance\" onClick={() => startRename(i)}>\n                                    \"Rename\"\n                        <td class=\"instance-port\">\n                            i.port\n                        <td>\n                            foreach d in db.designs\n                                if sys.id(d) == i.designId\n                                    <span class=\"design-label\">\n                                        d.label\n                        <td>\n                            <a class=\"open-instance\" href={sys.nest(\"/instances\", i.id)}>\n                                \"Open\"\n                            <button class=\"clone-instance\" onClick={() => sys.cloneInstance(i.id, newAppPort, newInfraPort)}>\n                                \"Clone\"\n                            <button class=\"delete-instance\" onClick={() => sys.delete(i.id)}>\n                                \"Delete\"\n\n    fn designSelector(instanceId, currentDesignId)\n        var state = { pick: currentDesignId }\n        fn render()\n            return <div class=\"design-selector\">\n                <select class=\"design-pick\" value={state.pick}>\n                    foreach d in db.designs\n                        <option value={sys.id(d)}>\n                            d.label\n                foreach d in db.designs\n                    if sys.id(d) == state.pick\n                        <button class=\"apply-design\" onClick={() => sys.setDesign(d, instanceId)}>\n                            \"Apply\"\n        return render\n\n    fn instanceSelectorPage()\n        var routeId = sys.toInt(sys.segment(path, 2))\n        return <main class=\"ide-instance\">\n            <h1>\n                \"Instance\"\n            <a class=\"back\" href=\"/instances\">\n                \"Back\"\n            foreach i in sys.instances\n                if i.id == routeId\n                    <span class=\"instance-app\">\n                        i.app\n                    designSelector(i.id, i.designId)()\n                    <button class=\"clone-instance\" onClick={() => sys.cloneInstance(i.id, newAppPort, newInfraPort)}>\n                        \"Clone\"\n                    <button class=\"delete-instance\" onClick={() => sys.delete(i.id)}>\n                        \"Delete\"\n\n    fn render()\n        var page\n        var section = sys.segment(path, 1)\n        var sub = sys.segment(path, 2)\n        if section == \"designs\"\n            if sub == \"\"\n                page = designsListPage\n            else\n                page = designEditorPage\n        else\n            if sub == \"\"\n                page = instancesListPage\n            else\n                page = instanceSelectorPage\n        return <div class=\"ide\">\n            navBar()\n            page()\n"
        types: [41, 47, 53, 59]
    Db 1
        designs: [13, 27, 39, 60]

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

    fn navBar()
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
            <table class="designs-table">
                <tr class="designs-head">
                    <th>
                        "Design"
                    <th>
                        "Actions"
                foreach d in db.designs
                    <tr class="design-row">
                        <td class="design-label">
                            d.label
                        <td>
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
            <table class="instances-table">
                <tr class="instances-head">
                    <th>
                        "Instance"
                    <th>
                        "Port"
                    <th>
                        "Design"
                    <th>
                        "Actions"
                foreach i in sys.instances
                    <tr class="instance-row">
                        <td>
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
                        <td class="instance-port">
                            i.port
                        <td>
                            foreach d in db.designs
                                if sys.id(d) == i.designId
                                    <span class="design-label">
                                        d.label
                        <td>
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
            navBar()
            page()
