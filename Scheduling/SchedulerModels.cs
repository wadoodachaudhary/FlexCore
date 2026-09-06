using System.ComponentModel.DataAnnotations;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
namespace Fx.ControlKit.Scheduling;
public enum SchedulerView { Day, Week, WorkWeek, MultiDay, Month, Timeline, Agenda }
public enum SchedulerChangeAction { Create, Update, Delete }
public sealed record SchedulerResource(string Id,string Text,string Color="#3274B9");
public sealed record SchedulerRange(DateTime Start,DateTime End,string TimeZoneId);
public sealed record SchedulerOccurrence(SchedulerEvent Event,DateTime Start,DateTime End,DateTime OriginalStart);
public sealed class SchedulerChangingArgs(SchedulerChangeAction action,SchedulerEvent item)
{ public SchedulerChangeAction Action{get;}=action;public SchedulerEvent Item{get;}=item;public bool Cancel{get;set;} }
public sealed class SchedulerEvent : IValidatableObject
{
 public string Id {get;set;}=Guid.NewGuid().ToString("N");
 [Required,MinLength(1)] public string Title {get;set;}="New appointment";
 public DateTime Start {get;set;}=DateTime.Today.AddHours(9);
 public DateTime End {get;set;}=DateTime.Today.AddHours(10);
 public bool AllDay {get;set;}
 public string TimeZoneId {get;set;}="UTC";
 public string? ResourceId {get;set;}
 public string? Description {get;set;}
 public string? RecurrenceRule {get;set;}
 public List<DateTime> ExcludedStarts {get;set;}=[];
 public string? SeriesId {get;set;}
 public SchedulerEvent Clone()=>new(){Id=Id,Title=Title,Start=Start,End=End,AllDay=AllDay,TimeZoneId=TimeZoneId,ResourceId=ResourceId,Description=Description,RecurrenceRule=RecurrenceRule,ExcludedStarts=ExcludedStarts.ToList(),SeriesId=SeriesId};
 public IEnumerable<ValidationResult> Validate(ValidationContext context)
 {
  if(End<=Start)yield return new("End must follow start.",[nameof(End)]);
  string? error=null;try{var zone=TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);if(zone.IsInvalidTime(DateTime.SpecifyKind(Start,DateTimeKind.Unspecified))||zone.IsInvalidTime(DateTime.SpecifyKind(End,DateTimeKind.Unspecified)))error="The appointment falls in a daylight-saving time gap.";}catch(Exception){error="Use a valid time-zone ID.";}if(error is not null)yield return new(error,[nameof(TimeZoneId)]);
  if(!string.IsNullOrWhiteSpace(RecurrenceRule)){error=null;try{var pattern=new RecurrencePattern(RecurrenceRule);if(!RecurrenceRule.Contains("FREQ=",StringComparison.OrdinalIgnoreCase))error="A recurrence frequency is required.";}catch(Exception e){error=e.Message;}if(error is not null)yield return new(error,[nameof(RecurrenceRule)]);}
 }
}
public static class SchedulerEngine
{
 public static IReadOnlyList<SchedulerOccurrence> Expand(IEnumerable<SchedulerEvent> events,SchedulerRange range,int maxOccurrences=20000)
 {
  if(range.End<=range.Start||maxOccurrences<1)throw new ArgumentException("The query range must be ordered and bounded.");
  var zone=TimeZoneInfo.FindSystemTimeZoneById(range.TimeZoneId);var result=new List<SchedulerOccurrence>();
  var from=TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(range.Start,DateTimeKind.Unspecified),zone);var until=TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(range.End,DateTimeKind.Unspecified),zone);
  var source=events.ToArray();if(source.Select(e=>e.Id).Distinct().Count()!=source.Length)throw new ArgumentException("Appointment IDs must be unique.");
  foreach(var item in source){Validator.ValidateObject(item,new(item),true);var icalZone=TimeZoneInfo.TryConvertWindowsIdToIanaId(item.TimeZoneId,out var ianaZone)?ianaZone:item.TimeZoneId;var calendar=new Ical.Net.Calendar();var entry=new CalendarEvent{Uid=item.Id,Summary=item.Title,DtStart=new CalDateTime(DateTime.SpecifyKind(item.Start,DateTimeKind.Unspecified),icalZone,!item.AllDay),DtEnd=new CalDateTime(DateTime.SpecifyKind(item.End,DateTimeKind.Unspecified),icalZone,!item.AllDay)};
   if(!string.IsNullOrWhiteSpace(item.RecurrenceRule))entry.RecurrenceRule=new RecurrencePattern(item.RecurrenceRule);foreach(var excluded in item.ExcludedStarts)entry.ExceptionDates.Add(new CalDateTime(DateTime.SpecifyKind(excluded,DateTimeKind.Unspecified),icalZone,!item.AllDay));calendar.Events.Add(entry);
   foreach(var occurrence in calendar.GetOccurrences(new CalDateTime(item.AllDay?DateTime.SpecifyKind(range.Start.Date,DateTimeKind.Unspecified):from))){var start=occurrence.Period.StartTime.AsUtc;var end=occurrence.Period.EffectiveEndTime?.AsUtc??start+(item.End-item.Start);var localStart=item.AllDay?occurrence.Period.StartTime.Value.Date:TimeZoneInfo.ConvertTimeFromUtc(start,zone);var localEnd=item.AllDay?(occurrence.Period.EffectiveEndTime?.Value.Date??localStart+(item.End.Date-item.Start.Date)):TimeZoneInfo.ConvertTimeFromUtc(end,zone);if(localStart>=range.End)break;if(localEnd<=range.Start)continue;if(result.Count>=maxOccurrences)throw new InvalidOperationException($"The scheduler range exceeds {maxOccurrences:N0} occurrences. Narrow the range.");result.Add(new(item,localStart,localEnd,occurrence.Period.StartTime.Value));}
  }
  return result.OrderBy(o=>o.Start).ThenBy(o=>o.End).ToArray();
 }
}
