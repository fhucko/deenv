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
        multiline bool
        order int

ui
    var newDesignId = 0
    var newInstanceName = ""
    var renameId = 0
    var renameName = ""
    var scalarTypes = ["text", "int", "bool", "decimal", "date", "dateTime", "password"]
    var typeKinds = ["object", "enum"]
    var cardinalities = ["single", "set", "dictionary"]
    var confirmDeleteId = 0
    var confirmDeleteInstanceId = 0

    fn addType(design)
        design.types.add({ name: "", baseType: "object", values: "", order: 0, props: [] })

    fn addProp(type)
        type.props.add({ name: "", type: "text", cardinality: "single", keyType: "", multiline: false, order: 0 })

    fn typeCardClass(type)
        if type.baseType == "enum"
            return "type-card is-enum"
        return "type-card"

    fn propRowClass(prop)
        if prop.cardinality == "dictionary"
            return "prop-row is-dict"
        if prop.type == "text" && prop.cardinality == "single"
            return "prop-row is-text-single"
        return "prop-row"

    fn kebabClass(open)
        if open
            return "kebab-menu open"
        return "kebab-menu"

    fn backdropClass(open)
        if open
            return "kebab-backdrop open"
        return "kebab-backdrop"

    fn startRename(i)
        renameId = i.id
        renameName = i.app

    fn doRename(i)
        sys.rename(i.id, renameName)
        renameId = 0

    fn navClass(active, name)
        if active == name
            return "nav-link is-active"
        return "nav-link"

    fn navBar(active)
        return <nav class="ide-nav">
            <a class={navClass(active, "instances")} href="/instances">
                "Instances"
            <a class={navClass(active, "designs")} href="/designs">
                "Designs"

    fn designsListPage()
        fn designActions(d)
            return <td class="design-actions">
                <a class="edit-design" href={sys.nest("/designs", sys.id(d))}>
                    "Edit"
                if confirmDeleteId == sys.id(d)
                    <span class="delete-confirm">
                        "Delete?"
                    <button class="delete-yes" onClick={() => db.designs.remove(d)}>
                        "Yes"
                    <button class="delete-cancel" onClick={() => confirmDeleteId = 0}>
                        "Cancel"
                else
                    <button class="delete-design" onClick={() => confirmDeleteId = sys.id(d)}>
                        "Delete"
        fn createDesignForm(draft)
            return <Field obj={draft} desc={sys.schema("Design", "label")}>
        return <main class="ide-designs">
            <h1>
                "Designs"
            <SetTable set={db.designs} desc={sys.schema("Design")} setPath="/designs" columns={["label"]} rowActions={designActions} createForm={createDesignForm}>

    fn designEditor(design)
        return <section class="design-editor">
            <input class="design-label" value={design.label}>
            <button class="add-type" onClick={() => addType(design)}>
                "+ Type"
            foreach type in design.types
                <div class={typeCardClass(type)}>
                    <div class="type-head">
                        <input class="type-name" value={type.name}>
                        <select class="type-kind" value={type.baseType}>
                            foreach k in typeKinds
                                <option value={k}>
                                    sys.humanize(k)
                        <button class="remove-type" onClick={() => design.types.remove(type)}>
                            "×"
                    <div class="enum-values">
                        <label class="values-label">
                            "Values (comma-separated)"
                        <input class="type-values" value={type.values}>
                    <div class="props-editor">
                        <div class="prop-head">
                            <span class="col-name">
                                "Name"
                            <span class="col-type">
                                "Type"
                            <span class="col-card">
                                "Cardinality"
                        foreach prop in type.props
                            <div class={propRowClass(prop)}>
                                <input class="prop-name" value={prop.name}>
                                <select class="prop-type" value={prop.type}>
                                    <optgroup label="Built-in">
                                        foreach s in scalarTypes
                                            <option value={s}>
                                                s
                                    <optgroup label="This design">
                                        foreach t in design.types
                                            <option value={t.name}>
                                                t.name
                                <select class="prop-cardinality" value={prop.cardinality}>
                                    foreach c in cardinalities
                                        <option value={c}>
                                            sys.humanize(c)
                                <input class="prop-keytype" value={prop.keyType}>
                                <button class="remove-prop" onClick={() => type.props.remove(prop)}>
                                    "×"
                                <label class="multiline-toggle">
                                    <input type="checkbox" class="prop-multiline" checked={prop.multiline}>
                                    "Multi-line text"
                        <button class="add-prop" onClick={() => addProp(type)}>
                            "+ Field"
            <details class="code-areas">
                <summary class="code-summary">
                    "Advanced (code)"
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
            if db.designs.any(d => sys.id(d) == routeId)
                foreach d in db.designs
                    if sys.id(d) == routeId
                        designEditor(d)
            else
                <p class="not-found">
                    "Design not found."

    fn instanceActions(inst, showOpen)
        var state = { open: false }
        fn closeMenu()
            state.open = false
            confirmDeleteInstanceId = 0
        fn pickRename()
            startRename(inst)
            closeMenu()
        fn pickClone()
            sys.cloneInstance(inst.id)
            closeMenu()
        fn render()
            return <div class="kebab">
                <button class="kebab-toggle" onClick={() => state.open = state.open == false}>
                    "⋯"
                <div class={backdropClass(state.open)} onClick={() => closeMenu()}>
                <div class={kebabClass(state.open)}>
                    if showOpen
                        <a class="open-instance" href={sys.nest("/instances", inst.id)}>
                            "Open"
                    <button class="rename-instance" onClick={() => pickRename()}>
                        "Rename"
                    <button class="clone-instance" onClick={() => pickClone()}>
                        "Clone"
                    if confirmDeleteInstanceId == inst.id
                        <span class="delete-confirm">
                            "Delete?"
                        <button class="delete-yes" onClick={() => sys.delete(inst.id)}>
                            "Yes"
                        <button class="delete-cancel" onClick={() => confirmDeleteInstanceId = 0}>
                            "Cancel"
                    else
                        <button class="delete-instance" onClick={() => confirmDeleteInstanceId = inst.id}>
                            "Delete"
        return render

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
                foreach d in db.designs
                    if sys.id(d) == newDesignId
                        <button class="create-instance" onClick={() => sys.create(d, newInstanceName)}>
                            "Create"
            <table class="instances-table">
                <tr class="instances-head">
                    <th>
                        "Instance"
                    <th>
                        "URL"
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
                        <td class="instance-port">
                            i.path
                        <td>
                            foreach d in db.designs
                                if sys.id(d) == i.designId
                                    <span class="design-label">
                                        d.label
                        <td class="row-actions">
                            <instanceActions inst={i} showOpen={true}>

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
            if sys.instances.any(i => i.id == routeId)
                foreach i in sys.instances
                    if i.id == routeId
                        <div class="instance-head">
                            if renameId == i.id
                                <input class="rename-input" value={renameName}>
                                <button class="rename-save" onClick={() => doRename(i)}>
                                    "Save"
                                <button class="rename-cancel" onClick={() => renameId = 0}>
                                    "Cancel"
                            else
                                <span class="instance-app">
                                    i.app
                            instanceActions(i, false)()
                        designSelector(i.id, i.designId)()
            else
                <p class="not-found">
                    "Instance not found."

    fn render()
        var page
        var active = "instances"
        var section = sys.segment(path, 1)
        var sub = sys.segment(path, 2)
        if section == "designs"
            active = "designs"
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
            navBar(active)
            page()
