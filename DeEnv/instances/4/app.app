types
    Db
        designs: set of Design
    Design
        label: text
        initialData: text
        common: text
        ui: text
        types: set of MetaType
    MetaType
        name: text
        baseType: text
        order: int
        props: set of MetaProp
    MetaProp
        name: text
        type: text
        cardinality: text
        keyType: text
        order: int

initialData
    Db 1
        designs: [10, 20]
    Design 10
        label: "instance"
        initialData: ""
        common: ""
        ui: "ui\n    fn render()\n        return <main>\n            \"hi\"\n"
        types: [11, 13]
    MetaType 11
        name: "Db"
        baseType: "object"
        order: 10
        props: [12]
    MetaProp 12
        name: "ready"
        type: "bool"
        order: 10
    MetaType 13
        name: "Item"
        baseType: "object"
        order: 20
        props: [14]
    MetaProp 14
        name: "label"
        type: "text"
        order: 10
    Design 20
        label: "crm"
        initialData: ""
        common: ""
        ui: ""
        types: [21]
    MetaType 21
        name: "Db"
        baseType: "object"
        order: 10
        props: [22]
    MetaProp 22
        name: "name"
        type: "text"
        order: 10

ui
    var newAppPort = 9100
    var newInfraPort = 9101

    fn addType(design)
        design.types.add({ name: "", baseType: "object", order: 0, props: [] })

    fn addProp(type)
        type.props.add({ name: "", type: "text", cardinality: "", keyType: "", order: 0 })

    fn listPage()
        return <main class="ide-list">
            <h1>
                "Instances"
            <a class="new-instance" href="/instances/new">
                "New instance"
            foreach i in sys.instances
                <div class="instance-row">
                    <span class="instance-app">
                        i.app
                    <span class="instance-port">
                        i.port
                    foreach d in db.designs
                        if d.label == i.app
                            <span class="design-label">
                                d.label
                            <a class="edit-instance" href={sys.nest("/instances", i.id)}>
                                "Edit"
                            <button class="publish-instance" onClick={() => sys.publish(d, i.id)}>
                                "Publish"
                    <button class="clone-instance" onClick={() => sys.cloneInstance(i.id, newAppPort, newInfraPort)}>
                        "Clone"
                    <button class="delete-instance" onClick={() => sys.delete(i.id)}>
                        "Delete"

    fn designEditor(design, instance)
        return <section class="design-editor">
            <h2 class="design-label">
                design.label
            <button class="add-type" onClick={() => addType(design)}>
                "Add type"
            foreach type in design.types
                <div class="type-row">
                    <input class="type-name" value={type.name}>
                    <input class="type-base" value={type.baseType}>
                    <button class="remove-type" onClick={() => design.types.remove(type)}>
                        "Remove type"
                    <button class="add-prop" onClick={() => addProp(type)}>
                        "Add prop"
                    foreach prop in type.props
                        <div class="prop-row">
                            <input class="prop-name" value={prop.name}>
                            <input class="prop-type" value={prop.type}>
                            <button class="remove-prop" onClick={() => type.props.remove(prop)}>
                                "Remove prop"
            <label class="ui-label">
                "UI"
            <input class="design-ui" value={sys.field(design, "ui")}>
            <label class="common-label">
                "Common"
            <input class="design-common" value={sys.field(design, "common")}>
            <label class="initial-label">
                "Initial data"
            <input class="design-initial" value={sys.field(design, "initialData")}>
            <button class="publish-design" onClick={() => sys.publish(design, instance.id)}>
                "Publish"

    fn editPage()
        var routeId = sys.toInt(sys.segment(path, 2))
        return <main class="ide-edit">
            <h1>
                "Edit instance"
            <a class="back" href="/instances">
                "Back"
            foreach i in sys.instances
                if i.id == routeId
                    foreach d in db.designs
                        if d.label == i.app
                            designEditor(d, i)

    fn newPage()
        return <main class="ide-new">
            <h1>
                "New instance"
            <a class="back" href="/instances">
                "Back"
            <p class="new-stub">
                "Create is coming soon"

    fn render()
        var page
        var seg = sys.segment(path, 2)
        if seg == "new"
            page = newPage
        else if seg == ""
            page = listPage
        else
            page = editPage
        return <div class="ide">
            page()
