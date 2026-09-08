namespace Vigilo.Classification;

/// <summary>
/// The exact JSON Schema for the single classification verdict. It is sent to the runtime
/// only when capability negotiation proves the selected runtime enforces
/// response_format json_schema; otherwise host-side parsing and validation remain the gate.
/// </summary>
public static class VerdictResponseSchema
{
    private const string Evidence = """
        {"type":"object","additionalProperties":false,"required":["segmentId","quote","role","occurrence"],"properties":{"segmentId":{"type":"string"},"quote":{"type":"string"},"role":{"enum":["primary","supporting"]},"occurrence":{"type":["integer","null"]}}}
        """;

    public static readonly string SingleVerdict = """
        {"type":"object","additionalProperties":false,"required":["messageType","hasObligation","requiresReply","mayEscalate","hasDeadline","deadlineExpression","normalizedDeadline","actionSummary","decisionReason","evidence"],"properties":{"messageType":{"enum":["unknown","newsletter","promotion","transactional","personal"]},"hasObligation":{"enum":["yes","no","uncertain"]},"requiresReply":{"enum":["yes","no","uncertain"]},"mayEscalate":{"enum":["yes","no","uncertain"]},"hasDeadline":{"enum":["yes","no","uncertain"]},"deadlineExpression":{"type":["string","null"]},"normalizedDeadline":{"type":["string","null"]},"actionSummary":{"type":"string"},"decisionReason":{"type":"string"},"evidence":{"type":"array","items":EVIDENCE_ITEM}}}
        """
        .Replace("EVIDENCE_ITEM", Evidence, StringComparison.Ordinal);
}
