using System.Text;
namespace Fx.ControlKit;

public sealed record MaskResult(string Text, string RawValue, bool Complete, string? Error);
/// <summary>Deterministic mask parsing shared by input controls and host validation.</summary>
public sealed class MaskPattern
{
 private sealed record Slot(char Symbol, bool Required, char Casing, bool Literal);
 private readonly List<Slot> _slots=[];
 public MaskPattern(string pattern)
 {
  ArgumentNullException.ThrowIfNull(pattern);var escape=false;char casing='|';
  foreach(var c in pattern){if(escape){_slots.Add(new(c,false,casing,true));escape=false;continue;}if(c=='\\'){escape=true;continue;}if(c is '>' or '<' or '|'){casing=c;continue;}_slots.Add(new(c,c is '0' or 'L' or 'A' or '&',casing,!"09#L?Aa&C".Contains(c)));}
  if(escape)throw new ArgumentException("A mask cannot end with an escape character.",nameof(pattern));
  if(_slots.Count==0)throw new ArgumentException("A mask must contain a literal or input slot.",nameof(pattern));
 }
 public MaskResult Apply(string? input,char prompt='_',bool includeLiterals=true)
 {
  input??="";var output=new StringBuilder();var raw=new StringBuilder();int index=0;bool complete=true,invalid=false;
  foreach(var slot in _slots){if(slot.Literal){output.Append(slot.Symbol);if(index<input.Length&&input[index]==slot.Symbol)index++;continue;}
   if(index<input.Length&&input[index]==prompt){index++;output.Append(prompt);complete&=!slot.Required;continue;}
   if(index>=input.Length){output.Append(prompt);complete&=!slot.Required;continue;}
   var c=input[index++];bool valid=slot.Symbol switch{'0' or '9'=>char.IsDigit(c),'#'=>char.IsDigit(c)||c is '+' or '-' or ' ','L' or '?'=>char.IsLetter(c),'A' or 'a'=>char.IsLetterOrDigit(c),_=>!char.IsControl(c)};
   if(!valid){invalid=true;output.Append(c);complete=false;continue;}c=slot.Casing=='>'?char.ToUpperInvariant(c):slot.Casing=='<'?char.ToLowerInvariant(c):c;raw.Append(c);output.Append(c);
  }
  if(index<input.Length){invalid=true;complete=false;}
  return new(includeLiterals?output.ToString():raw.ToString(),raw.ToString(),complete,invalid?"The value does not match the input mask.":!complete?"Complete the required mask positions.":null);
 }
}
