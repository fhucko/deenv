using System.Text.Json;
using System.Text.Json.Nodes;
using DeEnv.Code;
using DeEnv.Tests.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Code;

// Slice 1 lockstep gate: ClientState emits set|dict|list collection tags (not type:"array"+kind),
// and the client dt.ts merge reconstitutes the three sealed collection types by those tags.
public sealed class CollectionDtRoundTripTests
{
    [Test]
    public async Task ClientState_emits_set_dict_list_type_tags_not_array_kind()
    {
        var scope = new ExecScope();
        var ctx = new ExecContext();

        var member = new ExecObject { Id = 10, Props = new() { ["name"] = new ExecText { Value = "a" } } };
        var set = new ExecSet
        {
            Id = 100,
            ElementTypeName = "Item",
            Items = [new ExecItem { Key = 10, Value = member }],
        };
        var dictEntry = new ExecObject
        {
            Id = -50,
            Props = new() { ["__key"] = new ExecText { Value = "k" }, ["value"] = new ExecText { Value = "v" } },
            OwnerRef = 1, DictProp = "settings", Key = "k",
        };
        var dict = new ExecDict
        {
            Id = 200,
            ElementTypeName = "text",
            SourcePath = "/settings",
            OwnerRef = 1,
            DictProp = "settings",
            Items = [new ExecItem { Key = dictEntry.Id, Value = dictEntry }],
        };
        var list = new ExecList
        {
            Id = -7,
            Items =
            [
                new ExecItem { Key = 0, Value = new ExecInt { Value = 1 } },
                new ExecItem { Key = 1, Value = new ExecInt { Value = 2 } },
            ],
        };

        // Mark every item accessed so the collections ship full (output-position leaves).
        ctx.AccessedItems.Add((set, set.Items[0]));
        ctx.AccessedItems.Add((dict, dict.Items[0]));
        ctx.AccessedItems.Add((list, list.Items[0]));
        ctx.AccessedItems.Add((list, list.Items[1]));
        ctx.AccessedObjectProps.Add((member, "name"));
        ctx.AccessedObjectProps.Add((dictEntry, "__key"));
        ctx.AccessedObjectProps.Add((dictEntry, "value"));

        scope.Items["s"] = new ExecScopeItem { Value = set, IsReadOnly = true };
        scope.Items["d"] = new ExecScopeItem { Value = dict, IsReadOnly = true };
        scope.Items["l"] = new ExecScopeItem { Value = list, IsReadOnly = true };

        var state = ClientState.Serialize(scope, ctx);
        var leaves = (JsonObject)state["leaves"]!;
        await Assert.That(leaves.ContainsKey("collections")).IsTrue();
        await Assert.That(leaves.ContainsKey("arrays")).IsFalse();

        var collections = (JsonObject)leaves["collections"]!;
        await Assert.That(collections["100"]!["type"]!.GetValue<string>()).IsEqualTo("set");
        await Assert.That(collections["200"]!["type"]!.GetValue<string>()).IsEqualTo("dict");
        await Assert.That(collections["-7"]!["type"]!.GetValue<string>()).IsEqualTo("list");
        await Assert.That(collections["200"]!["ownerRef"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(collections["200"]!["dictProp"]!.GetValue<string>()).IsEqualTo("settings");

        // Scope refs use the collection type tags (not type:"array").
        await Assert.That(state["scope"]!["s"]!["value"]!["type"]!.GetValue<string>()).IsEqualTo("set");
        await Assert.That(state["scope"]!["d"]!["value"]!["type"]!.GetValue<string>()).IsEqualTo("dict");
        await Assert.That(state["scope"]!["l"]!["value"]!["type"]!.GetValue<string>()).IsEqualTo("list");

        // Lists remain ephemeral (negative id) in this slice.
        await Assert.That(list.Id).IsLessThan(0);
    }

    [Test]
    public async Task Client_mergeState_reconstitutes_set_dict_list_from_collection_tags()
    {
        var codeExecJs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "codeExec.js"));
        var dtJs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "dt.js"));

        // Minimal server payload shaped like ClientState.Serialize for set + dict + list.
        var payload = """
            {
              "leaves": {
                "objects": {
                  "10": { "props": { "name": { "type": "simple", "value": { "type": "text", "value": "a" } } } },
                  "-50": {
                    "props": {
                      "__key": { "type": "simple", "value": { "type": "text", "value": "k" } },
                      "value": { "type": "simple", "value": { "type": "text", "value": "v" } }
                    },
                    "ownerRef": 1, "dictProp": "settings", "key": "k"
                  }
                },
                "collections": {
                  "100": {
                    "type": "set", "elementTypeName": "Item",
                    "items": [ { "key": 10, "value": { "type": "object", "id": 10 } } ]
                  },
                  "200": {
                    "type": "dict", "elementTypeName": "text", "sourcePath": "/settings",
                    "ownerRef": 1, "dictProp": "settings",
                    "items": [ { "key": -50, "value": { "type": "object", "id": -50 } } ]
                  },
                  "-7": {
                    "type": "list",
                    "items": [
                      { "key": 0, "value": { "type": "simple", "value": { "type": "int", "value": 1 } } },
                      { "key": 1, "value": { "type": "simple", "value": { "type": "int", "value": 2 } } }
                    ]
                  }
                }
              },
              "scope": {
                "s": { "isReadOnly": true, "value": { "type": "set", "id": 100 } },
                "d": { "isReadOnly": true, "value": { "type": "dict", "id": 200 } },
                "l": { "isReadOnly": true, "value": { "type": "list", "id": -7 } }
              },
              "cache": []
            }
            """;

        var page = await SharedBrowser.NewPageAsync();
        await page.SetContentAsync("<!doctype html><html><body></body></html>");
        await page.AddScriptTagAsync(new() { Content = codeExecJs });
        // Minimal AppState shell (the production shape from init.ts) — avoid loading full init.js
        // (which would call connectWs / renderUi).
        await page.AddScriptTagAsync(new()
        {
            Content = """
                const uiStatic = {
                    lastId: { value: 0 },
                    cache: new Map(),
                    state: {
                        objects: {},
                        collections: {},
                        scope: { items: {}, parent: null, isTop: true },
                        localToServerIds: {},
                        serverToLocalIds: {}
                    }
                };
                """
        });
        await page.AddScriptTagAsync(new() { Content = dtJs });

        var resultJson = await page.EvaluateAsync<string>(
            $$"""
            () => {
                mergeState({{payload}});
                const st = uiStatic.state;
                const s = st.scope.items.s.value;
                const d = st.scope.items.d.value;
                const l = st.scope.items.l.value;
                return JSON.stringify({
                    hasCollectionsMap: st.collections != null && st.arrays == null,
                    sType: s.type, sId: s.id, sItems: s.items.length,
                    dType: d.type, dId: d.id, dOwner: d.ownerRef, dProp: d.dictProp,
                    lType: l.type, lId: l.id, lItems: l.items.map(i => i.value.value),
                    coll100: st.collections[100] && st.collections[100].type,
                    coll200: st.collections[200] && st.collections[200].type,
                    collNeg7: st.collections[-7] && st.collections[-7].type,
                });
            }
            """);

        var result = JsonDocument.Parse(resultJson).RootElement;
        await Assert.That(result.GetProperty("hasCollectionsMap").GetBoolean()).IsTrue();
        await Assert.That(result.GetProperty("sType").GetString()).IsEqualTo("set");
        await Assert.That(result.GetProperty("dType").GetString()).IsEqualTo("dict");
        await Assert.That(result.GetProperty("lType").GetString()).IsEqualTo("list");
        await Assert.That(result.GetProperty("sId").GetInt32()).IsEqualTo(100);
        await Assert.That(result.GetProperty("dOwner").GetInt32()).IsEqualTo(1);
        await Assert.That(result.GetProperty("dProp").GetString()).IsEqualTo("settings");
        await Assert.That(result.GetProperty("lId").GetInt32()).IsEqualTo(-7);
        await Assert.That(result.GetProperty("lItems")[0].GetInt32()).IsEqualTo(1);
        await Assert.That(result.GetProperty("lItems")[1].GetInt32()).IsEqualTo(2);
        await Assert.That(result.GetProperty("coll100").GetString()).IsEqualTo("set");
        await Assert.That(result.GetProperty("coll200").GetString()).IsEqualTo("dict");
        await Assert.That(result.GetProperty("collNeg7").GetString()).IsEqualTo("list");

        await page.Context.CloseAsync();
    }
}
