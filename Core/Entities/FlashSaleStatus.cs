using System.Runtime.Serialization;

namespace Core.Entities
{
    public enum FlashSaleStatus
    {
        [EnumMember(Value = "Scheduled")]
        Scheduled,
        [EnumMember(Value = "Active")]
        Active,
        [EnumMember(Value = "Ended")]
        Ended
    }
}
