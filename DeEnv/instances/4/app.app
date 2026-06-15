types
    Db
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

ui
    var appPort = 9100
    var infraPort = 9101
    var cloneAppPort = 9200
    var cloneInfraPort = 9201

    fn addType()
        db.types.add({ name: "", baseType: "object", order: 0, props: [] })

    fn addProp(type)
        type.props.add({ name: "", type: "text", cardinality: "", keyType: "", order: 0 })

    fn typeEditor()
        return <section class="types">
            <h2>
                "Types"
            <button class="add-type" onClick={addType}>
                "Add type"
            foreach type in db.types
                <div class="type-row">
                    <input class="type-name" value={type.name}>
                    <input class="type-base" value={type.baseType}>
                    <button class="remove-type" onClick={() => db.types.remove(type)}>
                        "Remove type"
                    <button class="add-prop" onClick={() => addProp(type)}>
                        "Add prop"
                    foreach prop in type.props
                        <div class="prop-row">
                            <input class="prop-name" value={prop.name}>
                            <input class="prop-type" value={prop.type}>
                            <button class="remove-prop" onClick={() => type.props.remove(prop)}>
                                "Remove prop"

    fn instanceList()
        return <section class="instances">
            <h2>
                "Instances"
            <input class="clone-app-port" value={cloneAppPort}>
            <input class="clone-infra-port" value={cloneInfraPort}>
            foreach i in sys.instances
                <div class="instance-row">
                    <span class="instance-app">
                        i.app
                    <span class="instance-port">
                        i.port
                    <button class="clone-instance" onClick={() => sys.cloneInstance(i.id, cloneAppPort, cloneInfraPort)}>
                        "Clone"
                    <button class="publish-instance" onClick={() => sys.publish(db, i.id)}>
                        "Publish"
                    <button class="delete-instance" onClick={() => sys.delete(i.id)}>
                        "Delete"
            <div class="create-form">
                <input class="app-port" value={appPort}>
                <input class="infra-port" value={infraPort}>
                <button class="create-instance" onClick={() => sys.create(db, appPort, infraPort)}>
                    "Create instance"

    fn render()
        return <main class="designer">
            <h1>
                "Designer"
            typeEditor()
            instanceList()
