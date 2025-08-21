# Mapfy


## Examplo de uso

Program.cs

```
using Mapfy;


public class CEP
{
    public string Codigo { get; set; }
}
public class CEPDto
{
    public string Codigo { get; set; }
}
public class Address
{
    public string City { get; set; }
    public string State { get; set; }
    public CEP Cep { get; set; }
}

public class AddressDto
{
    public string City { get; set; }
    public string Province { get; set; }
    public CEPDto Cep { get; set; }
}

public class Person
{
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public int Age { get; set; }
    public string Email { get; set; }
    public string Code; // field
    public Address? Address { get; set; }
    public List<string> Tags { get; set; }
    public string[] Aliases { get; set; }
}

public class PersonDto
{
    public string FullName { get; set; }
    public int Age { get; set; }
    public string Email { get; set; }
    public string Code; // field
    public string City { get; set; }
    public AddressDto? Address { get; set; }
    public List<string> Tags { get; set; }
    public string[] Aliases { get; set; }

}

internal static class Demo
{
    public static void Run()
    {
        var cfg = new MapperConfiguration(c =>
        {

            c.For<CEP, CEPDto>()
                .Strategy(MappingStrategy.CompiledExpressions)
                .CaseInsensitive()
            //.ForMember(d => d.Codigo, o => o.MapFrom(s => s.Codigo))
            ;

            c.For<Address, AddressDto>()
                .Strategy(MappingStrategy.CompiledExpressions)
                .CaseInsensitive()
                .ForMember(d => d.Province, o => o.MapFrom(s => s.State))
            //.ForMember(d => d.City, o => o.MapFrom(s => s.City))
            ;


            c.For<Person, PersonDto>()
                .Strategy(MappingStrategy.CompiledExpressions)
                .CaseInsensitive()
                .ForMember(d => d.FullName, o => o.MapFrom(s => s.FirstName + " " + s.LastName))
                .ForMember(d => d.Code, o => o.MapFrom(s => s.Code));

        });

        var mapper = cfg.CreateMapper();
        MapfyContext.SetDefault(mapper); // enable p.Map<TDest>()

        var p = new Person()
        {
            Code = "X-42",
            FirstName = "Ada",
            LastName = "Lovelace",
            Age = 36,
            Email = "ada@example.com",
            Address = new Address { City = "London", State = "ENG", Cep = new CEP() { Codigo = "03132080" } },
            Tags = new List<string> { "math", "programming" },
            Aliases = new[] { "Augusta", "Ada L." }
        };

        // 1) Original style
        var dto = mapper.Map<Person, PersonDto>(p);

        // 2) Short style using runtime source type
        var dto2 = mapper.Map<PersonDto>(p);

        // 3) Object extension via global context
        var dto3 = p.Map<PersonDto>();

        Console.WriteLine("FullName: " + dto3.FullName);
        Console.WriteLine("Age: " + dto3.Age);
        Console.WriteLine("Email: " + dto3.Email);
        Console.WriteLine("Code: " + dto3.Code);
        Console.WriteLine("City: " + dto3.City);
        Console.WriteLine("Address.City: " + (dto3.Address != null ? dto3.Address.City : null) + ", Province: " + (dto3.Address != null ? dto3.Address.Province : null));
        Console.WriteLine("Tags: " + string.Join(",", dto3.Tags));
        Console.WriteLine("Aliases: " + string.Join(",", dto3.Aliases));

        var many = new[]
        {
            new Person()   {
                Code = "A-1",
            FirstName = "Ada",
            LastName = "Lovelace",
            Age = 36,
            Email = "ada@example.com",
        },
            new Person()   {
                Code ="B-2",
            FirstName = "Ada",
            LastName = "Lovelace",
            Age = 36,
            Email = "ada@example.com",
        }
        };

        var dtos = mapper.MapList<Person, PersonDto>(many);
        Console.WriteLine("Mapped list count: " + dtos.Count);
    }
}

public static class Program
{
    public static void Main()
    {
        Demo.Run();
    }
}




```