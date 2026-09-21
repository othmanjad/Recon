namespace Recon.Web;

/// <summary>
/// The rule builder's vocabulary, in words rather than in enum names.
///
/// <para>
/// <c>MarkAmbiguous</c>, <c>NumericTolerance</c> and <c>MinorUnit</c> are the
/// database's words and the code's words, and they are right there — but the
/// person configuring a reconciliation is an Operations user, and a screen
/// that shows them the enum is a screen that assumes they read the schema. The
/// stored value does not change; only what is drawn beside it does, so nothing
/// downstream has a second vocabulary to learn.
/// </para>
///
/// <para>
/// Bilingual, Arabic first, because that is who uses it — with the English
/// term kept after it so that a message from a log, a column in the database
/// and a line on the screen can still be recognised as the same thing.
/// </para>
/// </summary>
public static class RuleWords
{
    public static string Mode(string value) => value switch
    {
        "Row" => "صف بصف · Row",
        "Aggregate" => "مجموع بمجموع · Aggregate",
        _ => value,
    };

    public static string OnMultiple(string value) => value switch
    {
        "MarkAmbiguous" => "عند التعدد: بلّغ ولا تختر · MarkAmbiguous",
        "TakeEarliest" => "عند التعدد: خذ الأقدم · TakeEarliest",
        "Fail" => "عند التعدد: أفشل التشغيل · Fail",
        _ => value,
    };

    /// <summary>
    /// The comparison, and what it costs. An operator choosing between
    /// <c>Exact</c> and <c>Contains</c> is choosing between a seek and a scan,
    /// which at two million rows is the difference between a minute and an
    /// hour — so the label says so rather than leaving it to a tooltip.
    /// </summary>
    public static string Comparison(string value) => value switch
    {
        "Exact" => "متطابق تماماً · Exact",
        "NumericExact" => "نفس الرقم · NumericExact",
        "NumericTolerance" => "رقم بفارق مسموح · NumericTolerance",
        "DateExact" => "نفس التاريخ · DateExact",
        "DateWithin" => "تاريخ خلال مدة · DateWithin",
        "StartsWith" => "يبدأ بـ · StartsWith",
        "EndsWith" => "ينتهي بـ · EndsWith ⚠ مسح كامل",
        "Contains" => "يحتوي · Contains ⚠ مسح كامل",
        _ => value,
    };

    public static string Unit(string value) => value switch
    {
        "MinorUnit" => "وحدة صغرى (فلس) · MinorUnit",
        "Minute" => "دقيقة · Minute",
        "Hour" => "ساعة · Hour",
        "Day" => "يوم · Day",
        _ => value,
    };

    public static string Side(string value) => value switch
    {
        "Left" => "الطرف الأيسر · Left",
        "Right" => "الطرف الأيمن · Right",
        "Both" => "الطرفان · Both",
        _ => value,
    };
}
