using System.Net;
using System.Text;
using GraphQL; // GraphQLRequest
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Sitecore.API.Foundation.Authorization.Abstractions;
using Sitecore.API.Foundation.Authorization.Models;
using Sitecore.API.Foundation.GraphQL.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace Sitecore.API.Foundation.GraphQL.Tests;

public class VariablesShapeTests
{
    private sealed class CaptureRequestHandler : HttpMessageHandler
    {
        private readonly string _responseJson;
        public string? LastRequestBody { get; private set; }
        public CaptureRequestHandler(string responseJson) => _responseJson = responseJson;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json")
            };
        }
    }

    public class CreateItemInput
    {
        public string Database { get; set; } = string.Empty;
        public List<FieldValueInput> Fields { get; set; } = new();
        public string Language { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public Guid Parent { get; set; }
        public Guid TemplateId { get; set; }
    }

    public class FieldValueInput
    {
        public string Name { get; set; } = string.Empty;
        public bool? Reset { get; set; }
        public string Value { get; set; } = string.Empty;
    }

    private static (IServiceProvider sp, CaptureRequestHandler handler) BuildProvider()
    {
        var services = new ServiceCollection();

        services.AddSitecoreGraphQL(o =>
        {
            o.Endpoint = "http://unit.test/graphql";
            o.ClientId = "id";
            o.ClientSecret = "secret";
            o.EnableUnauthorizedRefresh = false;
        });

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService.Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()))
                    .ReturnsAsync(default(SitecoreAuthToken));
        services.AddSingleton(tokenService.Object);
        services.AddSingleton(Mock.Of<ITokenValueAccessor>());

        var handler = new CaptureRequestHandler("{ \"data\": { \"noop\": true } }");
        services.AddHttpClient(SitecoreGraphQLFactory.NamedHttpClient)
                .ConfigurePrimaryHttpMessageHandler(() => handler);

        return (services.BuildServiceProvider(), handler);
    }

    [Fact]
    public async Task Variables_anonymous_object_uses_input_property_name()
    {
        // Arrange
        var (sp, handler) = BuildProvider();
        var factory = sp.GetRequiredService<ISitecoreGraphQLFactory>();
        var client = await factory.CreateClientAsync();

        var parent = Guid.NewGuid();
        var template = Guid.NewGuid();
        var input = new CreateItemInput
        {
            Database = "master",
            Language = "en",
            Name = "ItemName",
            Parent = parent,
            TemplateId = template,
            Fields = new List<FieldValueInput>
            {
                new FieldValueInput { Name = "Title", Value = "Hello" },
                new FieldValueInput { Name = "Resettable", Reset = true, Value = "ignored" }
            }
        };

        var request = new GraphQLRequest
        {
            Query = "mutation ($input: CreateItemInput!) { createItem(input: $input) { id } }",
            Variables = new { input = input } // explicit property name as required by GraphQL operation
        };

        // Act
        _ = await client.SendMutationAsync<JObject>(request);

        // Assert
        handler.LastRequestBody.ShouldNotBeNull();
        var root = JObject.Parse(handler.LastRequestBody!);
        root["variables"].ShouldNotBeNull();
        var inputEl = root["variables"]!["input"]!;
        inputEl.ShouldNotBeNull();

        // Ensure nested content exists and GUIDs are serialized as strings
        var fieldsEl = inputEl["Fields"] ?? inputEl["fields"];
        fieldsEl.ShouldNotBeNull();
        fieldsEl!.Type.ShouldBe(JTokenType.Array);
        fieldsEl!.Count().ShouldBe(2);

        var parentEl = inputEl["Parent"] ?? inputEl["parent"];
        parentEl.ShouldNotBeNull();
        parentEl!.Type.ShouldBe(JTokenType.String);
        parentEl!.Value<string>().ShouldBe(parent.ToString("D"));

        var templateEl = inputEl["TemplateId"] ?? inputEl["templateId"];
        templateEl.ShouldNotBeNull();
        templateEl!.Type.ShouldBe(JTokenType.String);
        templateEl!.Value<string>().ShouldBe(template.ToString("D"));
    }
}
