// Mirror of the C# enums (DeEnv/Instance/InstanceDescription.cs, DeEnv/Code/CodeAst.cs).
// String-valued, matching the camelCase wire form produced by SchemaJson.Options, so
// the same JSON deserializes identically on the server (C#) and the client (TS).
// Keep in lockstep with the C# enums — there are no per-member attributes on either side.

export enum BaseType {
    Bool = "bool",
    Int = "int",
    Decimal = "decimal",
    Text = "text",
    Date = "date",
    DateTime = "dateTime",
    Object = "object",
}

export enum Cardinality {
    Single = "single",
    Dictionary = "dictionary",
    Set = "set",
}

export enum CodeInfixOpType {
    Add = "add",
    Subtract = "subtract",
    Multiply = "multiply",
    Divide = "divide",
    Modulo = "modulo",
    Equals = "equals",
    NotEquals = "notEquals",
    MoreThan = "moreThan",
    MoreThanOrEqual = "moreThanOrEqual",
    LessThan = "lessThan",
    LessThanOrEqual = "lessThanOrEqual",
    And = "and",
    Or = "or",
    ObjectProp = "objectProp",
}
