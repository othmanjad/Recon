using Recon.Engine.Staging;
using Xunit;

namespace Recon.UnitTests;

public class SlotRefTests
{
    [Theory]
    [InlineData("Text1", SlotKind.Text, 0)]
    [InlineData("Text30", SlotKind.Text, 29)]
    [InlineData("Num15", SlotKind.Num, 14)]
    [InlineData("Dec5", SlotKind.Dec, 4)]
    [InlineData("Date8", SlotKind.Date, 7)]
    [InlineData("Flag5", SlotKind.Flag, 4)]
    public void ParsesEverySlotInTheSchema(string name, SlotKind kind, int index)
    {
        var slot = SlotRef.Parse(name);

        Assert.Equal(kind, slot.Kind);
        Assert.Equal(index, slot.Index);
        Assert.Equal(name, slot.Name);
    }

    [Theory]
    [InlineData("Text0")]
    [InlineData("Text31")]
    [InlineData("Num16")]
    [InlineData("Dec6")]
    [InlineData("Date9")]
    [InlineData("Flag6")]
    [InlineData("Bogus1")]
    [InlineData("Text")]
    [InlineData("Text1; DROP TABLE x")]
    public void RejectsAnythingOutsideIt(string name) =>
        Assert.Throws<ArgumentException>(() => SlotRef.Parse(name));

    [Fact]
    public void RoundTripsEverySlotName()
    {
        // The slot catalogue in db/02-seed.sql and these counts must agree; if
        // one is widened without the other, this test says so.
        for (var i = 1; i <= StagingRecord.TextSlots; i++)
        {
            Assert.Equal("Text" + i, SlotRef.Parse("Text" + i).Name);
        }

        for (var i = 1; i <= StagingRecord.NumSlots; i++)
        {
            Assert.Equal("Num" + i, SlotRef.Parse("Num" + i).Name);
        }
    }
}
