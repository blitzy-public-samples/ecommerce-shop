using System.Runtime.Serialization;

namespace Core.Entities
{
    public enum ReservationStatus
    {
        [EnumMember(Value = "Active")]
        Active,
        [EnumMember(Value = "Committed")]
        Committed,
        [EnumMember(Value = "Expired")]
        Expired,
        [EnumMember(Value = "Cancelled")]
        Cancelled
    }
}
