using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

// aleksei-sakharov general comments:
// 1) All classes and instances should be specified in a separate file.
//    All of them should be structured with a separation by sub-folders and namespaces (Interfaces, DataAccess, Services, etc.)
//    If this Api expects to grow, then it's better to also extract classes to a separate projects (Models and common helpers to Common; FiguresStorage to DataAccess; etc.)
// 2) IRedisClient and IOrderStorage:
//    If some implementation is expected in scope of this repo, then implementation should be added to this repo
//    If implementation is expected in some external package, then this package should be referenced and implementation to be registered in a Startup
// 3) Implement unit-tests to cover implemented logic (including multi-thread tests as Api can receive a lot of requests)
// 4) Implement Integration Test to ensure Api setup
// 5) <summary> is recommended if we are going to expose any services/helpers exernally
// 6) DTOs (Order, Figure) can be immutable (depends on Company Policy). For example, C# 9.0 supports init-only properties

namespace FiguresDotStore.Controllers
{
	// aleksei-sakharov: In general total count of current items says nothing. It's much better to use some entity storage (e.g. Stack or Queue).
	//                   As we are developing an online shop, this entity may have some exact id. Shop back office will know what exact instance of figure to be sent.
	//                   TBH, I even don't see a case to use Redis this way here. If we want to use Redis as a replacement for the database, then we need to store entities there.
	//                   Each entity may contain: Id, Type, Some useful info, Status (New, Reserved, Sold)
	//                   Then IRedisClient can provide following methods:
	//                   - Get(id)
	//                   - Set(id, storageEntity)
	//                   IRedisClient is not required to be a thread-safe. However, FiguresStorage must to be a thread-safe
	//                   (all methods of FiguresStorage should use Double-Checked Locking or similar approach.
	//                   - public CreateNew (for back office to populate storage with new entities). If not exists -> lock -> if still not exists -> add
	//                   - private Reserve(id) (on user reservation start) - It should be thread safe operation. If status new -> Lock -> if still new -> reserve
	//                   - public int ReserveNext(type) -> This method to be used by controller on add item to a cart event.
	//                                                     As a helper it may use some private queue that will be populated by CreateNew method
	//                                                     In the result it should expose an exact id of reserved instance to cart
	//                   - public CancelReservation(id) (Reserved -> New transaction)
	//                   - public SaveById (Reserved -> Sold)
	//                   IRedisClient it-self should use Id as a primary key.
	internal interface IRedisClient
	{
		int Get(string type);
		void Set(string type, int current);
	}

	// aleksei-sakharov: 1) Design: such type of classes are usually implemented with a Service pattern.
	//                     It shouldn't be static and should have IFiguresStorage interface.
	//                     IFiguresStorage to be used then in FiguresController class. It will also simplify a unit-testing of controller class
	//                     IFiguresStorage to FiguresStorage registration should be added in Sturtup class
	//                  2) Logic: This class is not thread safe. Between CheckIfAvailable and Reserve calls everything can happen
	public static class FiguresStorage
	{
		// aleksei-sakharov: (Depends on company policy) From my point of view it's better to write all comments in English. Do you speak English? :)
		// корректно сконфигурированный и готовый к использованию клиент Редиса
		// aleksei-sakharov: 1) please, align private fileds to a common style.
		//                      Here you are using PascalCase and controller is using _camelCase. Exact style should be in a Company Policy
		//                   2) private auto-property doesn't have enough reasons to exists. It's better to use private readonly filed.
		//                      Private filed will also make generated IL shorter as auto get-only property is producing both: private filed and private get_RedisClient() method
		//                   3) all fields should be initialized in constructor. So it you re-work FiguresStorage to a Service pattern, you may inject IRedisClient via contructor
		private static IRedisClient RedisClient { get; }

		// aleksei-sakharov: No need in this method if we rewrite IRedisClient with proposed logic
		public static bool CheckIfAvailable(string type, int count)
		{
			return RedisClient.Get(type) >= count;
		}

		public static void Reserve(string type, int count)
		{
			var current = RedisClient.Get(type);

			RedisClient.Set(type, current - count);
		}
	}

	// aleksei-sakharov: In general, Position and Cart entities are used as a DTO now and imidiately convered to Figures and Order.
	//                   We can get rid of Position and Cart entities by using "Custom Model Binding" pf ASP.NET Core and FiguresFactory that I have descried below
	//                   As a bonus of "Custom Model Binding" you can do a model validation inside this parsing logic and add ValidationState to a parsed model.
	//                   Controller will use ModelState.IsValid instead of current direct call to Validate method
	public class Position
	{
		// aleksei-sakharov: Enum should be used for such selector fields
		public string Type { get; set; }

		// aleksei-sakharov: If order postion expects that it can be any figure, I would use Dementions property name.
		//                   This property should be an array then. Each figure will start validation with checking that dementions amunt suites Figure needs.
		public float SideA { get; set; }
		public float SideB { get; set; }
		public float SideC { get; set; }

		// aleksei-sakharov: Validation: Count should be greate than zero
		public int Count { get; set; }
	}

	public class Cart
	{
		public List<Position> Positions { get; set; }
	}

	public class Order
	{
		// aleksei-sakharov: That's bad to use the same name as in Cart. It's better to rename to Figures for more readable code
		public List<Figure> Positions { get; set; }

		public decimal GetTotal() =>
			Positions.Select(p => p switch
				{
					// aleksei-sakharov: 1.2m and 0.9m multiplier can be defined in Figure class (or IFigure interface). This multiplier should have a proper name (e.g. CostPerSquareMeter)
					//                   Each derived class define exact value then. You will not need an additional cast operation here.
					//                   You will call Select(p => p.GetPrice()). GetPrice - can have other name. That's just my assumption that you have a price here
					Triangle => (decimal) p.GetArea() * 1.2m,
					Circle => (decimal) p.GetArea() * 0.9m
					// aleksei-sakharov: Not all cases are handled in this switch
				})
			    // aleksei-sakharov: There is a method with selector, so you can use 'Sum(p => p.GetPrice())' instead of current Select(..).Sum()
				.Sum();
	}

	// aleksei-sakharov: 1) as switch is used in controller to create derived instance, then Factory pattern can be used
	//                      `public static Figure CreateFigure(position)` method to be created in new FiguresFactory class.
	//                      As for other classes, this should be a Service with proper interface
	//                      This method desides what derived type to create
	//                   2) If we want to allow easy extension of Figure variations, we can create an interface IFigure
	//                      Each derived class to be marked with some Type class attribute
	//                      [AttributeUsage(AttributeTargets.Class)]
	//                      public class PositionTypeAttribute : Attribute
	//                      {
	//                          public PositionType PositionType { get; set; }
	//                      }
	//                      Then FiguresFactory can use reflection to load all posible IFigure implementations and search for proper attribute on one of them
	//                   3) SideA, SideB, SideC are not common for different types of figures, so they cannot be present in a base class
	//                   4) abstract class is needed oly if we have some implementation on it. In this case we don't have, so it can be converted to an interface
	public abstract class Figure
	{
		// aleksei-sakharov: Does 'float' upper limit enough for our business needs? Contact analitics on it
		public float SideA { get; set; }
		public float SideB { get; set; }
		public float SideC { get; set; }

		public abstract void Validate();
		public abstract double GetArea();
	}

	public class Triangle : Figure
	{
		// aleksei-sakharov: method should validate negative values as well
		public override void Validate()
		{
			// aleksei-sakharov: MINOR: can be a staic inline function
			bool CheckTriangleInequality(float a, float b, float c) => a < b + c;
			if (CheckTriangleInequality(SideA, SideB, SideC)
			    && CheckTriangleInequality(SideB, SideA, SideC)
			    && CheckTriangleInequality(SideC, SideB, SideA)) 
				return;
			// aleksei-sakharov: "restrictions are not" or "restrictions were not". The same for all other occurances of this issue
			//                   Message template can be extracted and used by all Figures
			//                   Surprise for the attentive reader: Thank you if you have read all my comments in this file!!!
			//                   Remember, that a good application start with a good design! ;)
			throw new InvalidOperationException("Triangle restrictions not met");
			// aleksei-sakharov: Idelly, we also need to have an apper limit checked
		}

		public override double GetArea()
		{
			var p = (SideA + SideB + SideC) / 2;
			// aleksei-sakharov: float * float returns float. So you can get an overflow here
			return Math.Sqrt(p * (p - SideA) * (p - SideB) * (p - SideC));
		}
		
	}
	
	public class Square : Figure
	{
		public override void Validate()
		{
			// aleksei-sakharov: Does empty square SideA is valid? I don't think so :)
			if (SideA < 0)
				throw new InvalidOperationException("Square restrictions not met");

			// aleksei-sakharov: If you will get rig of SideA, SideB, SideC in the base class and use Dementions array, then you will need 1 check that array has exactly 1 value.
			//                   You don't need the second value for a square
			if (SideA != SideB)
				throw new InvalidOperationException("Square restrictions not met");

			// aleksei-sakharov: Idelly, we also need to have an apper limit checked
		}

		// aleksei-sakharov: float * float returns float. So you can get an overflow here
		public override double GetArea() => SideA * SideA;
	}
	
	public class Circle : Figure
	{
		public override void Validate()
		{
			// aleksei-sakharov: Does empty circle SideA is valid? I don't think so :)
			if (SideA < 0)
				throw new InvalidOperationException("Circle restrictions not met");
			// aleksei-sakharov: Idelly, we also need to have an apper limit checked
		}

		public override double GetArea() => Math.PI * SideA * SideA;
	}

	public interface IOrderStorage
	{
		// сохраняет оформленный заказ и возвращает сумму
		Task<decimal> Save(Order order);
	}
	
	[ApiController]
	[Route("[controller]")]
	// aleksei-sakharov: (UX comment) an overall logic of this controller is strange. User is trying to save some cart -> We are saving it (if we can) -> Then user know the price
	//                   That's very bad pattern from the user perspective. In general, such kind of reservation system should at least allow to
	//                   1) Get a single Figure Price based on User provided Dementions (some GET method)
	//                   2) Initialize a cart (POST method) to be able to add/remove items to/from this cart
	//                   3) Remove the cart (DELETE method) to clean all reserved figures
	//                   4) Add item to a cart (POST or PUT method, depends o a Company Policy) and remove it from there DELETE or PUT method
	//                   5) GET cart method to get current state of a cart with a total price
	//                   6) save cart (POST or PUT method) to save pre-created cart as ordered. This method should work with FiguresStorage to validate if Figures are available before saving
	public class FiguresController : ControllerBase
	{
		// aleksei-sakharov: logger is not used. In general, it's better to log some info in controller and underlying services
		private readonly ILogger<FiguresController> _logger;
		private readonly IOrderStorage _orderStorage;

		public FiguresController(ILogger<FiguresController> logger, IOrderStorage orderStorage)
		{
			_logger = logger;
			_orderStorage = orderStorage;
		}

		// хотим оформить заказ и получить в ответе его стоимость
		[HttpPost]
		// aleksei-sakharov: Currently this method logic is
		//                   -- check if figures are available
		//                   -- convert input cart and validate it's content
		//                   -- make an order reservation
		//                   It's better to re-work this order and move input data conversion and validation to the first place.
		//                   Also figures check and reservation should be thread-safe set of operations
		//                   Input data convertion can be done 3 ways:
		//                   1) FiguresFactory call right here at the beginning of the controller method
		//                   2) Separate Mapper service that will map Position class to Figure
		//                   3) "Custom Model Binding" of ASP.NET Core that I have described above
		public async Task<ActionResult> Order(Cart cart)
		{
			foreach (var position in cart.Positions)
			{
				if (!FiguresStorage.CheckIfAvailable(position.Type, position.Count))
				{
					return new BadRequestResult();
				}
			}

			var order = new Order
			{
				Positions = cart.Positions.Select(p =>
				{
					// aleksei-sakharov: See my coment about Factory pattern
					Figure figure = p.Type switch
					{
						"Circle" => new Circle(),
						"Triangle" => new Triangle(),
						"Square" => new Square()
						// aleksei-sakharov: switch shuld validate 'default' case.
						//                   If client application is sending unknown position Type we should report it back to a client application
					};
					figure.SideA = p.SideA;
					figure.SideB = p.SideB;
					figure.SideC = p.SideC;
					// aleksei-sakharov: It's an input data validation. Should be performed as a first action of this method before FiguresStorage.CheckIfAvailable call (see full method comment)
					figure.Validate();
					return figure;
				}).ToList()
			};

			foreach (var position in cart.Positions)
			{
				FiguresStorage.Reserve(position.Type, position.Count);
			}

			var result = _orderStorage.Save(order);

			return new OkObjectResult(result.Result);
		}
	}
}
