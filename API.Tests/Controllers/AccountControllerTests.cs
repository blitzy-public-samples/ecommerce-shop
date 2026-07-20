using System.Collections.Generic;
using System.Threading.Tasks;
using API.Controllers;
using API.Dtos;
using API.Errors;
using API.Tests.Helpers;                 // ControllerTestHelpers (Identity manager mocks, claims principals, async Users)
using AutoMapper;
using Core.Entities.Identity;            // AppUser, Address (Identity.Address — NOT OrderAggregate)
using Core.Interfaces;                   // ITokenService
using FluentAssertions;
using Microsoft.AspNetCore.Identity;     // UserManager, SignInManager, IdentityResult, IdentityError
                                         // (SignInResult is referenced fully-qualified inline to avoid the
                                         //  Microsoft.AspNetCore.Mvc.SignInResult ambiguity — see below)
using Microsoft.AspNetCore.Mvc;          // ActionResult<T> and the *ObjectResult result types
using Moq;
using Xunit;

namespace API.Tests.Controllers
{
    /// <summary>
    /// Unit tests for <see cref="AccountController"/> — the most collaborator-heavy controller in the
    /// API presentation layer. The controller depends on <see cref="UserManager{AppUser}"/>,
    /// <see cref="SignInManager{AppUser}"/>, <see cref="ITokenService"/> and
    /// <see cref="IMapper"/>; every collaborator is replaced with a Moq test double so each action
    /// is exercised in complete isolation (no database, no HTTP pipeline, no external service).
    /// <para>
    /// The Identity manager mocks and authenticated <see cref="System.Security.Claims.ClaimsPrincipal"/>
    /// values are sourced from the sibling helper <see cref="ControllerTestHelpers"/> (AAP §0.4.4), and
    /// the auth-aware actions (<c>GetCurrentUser</c>/<c>GetUserAddress</c>/<c>UpdateUserAddress</c>) rely
    /// on <see cref="ControllerTestHelpers.MockUserManagerUsers"/> to expose an async-queryable
    /// <c>UserManager.Users</c> so the static <c>UserManagerExtensions</c> methods resolve.
    /// </para>
    /// <para>
    /// Tests follow the <c>MethodName_StateUnderTest_ExpectedBehavior</c> convention with an explicit
    /// Arrange-Act-Assert layout and FluentAssertions. Fresh mocks are constructed for every test via
    /// <see cref="CreateController"/>, guaranteeing no shared mutable state between tests.
    /// </para>
    /// </summary>
    public class AccountControllerTests
    {
        // ------------------------------------------------------------------
        // Arrange helper — fresh mocks + controller per test (no shared state)
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds a fully-mocked <see cref="AccountController"/> and surfaces the underlying mocks so a
        /// test can configure only the collaborators it cares about. A brand-new set of mocks is created
        /// on every invocation, satisfying the "fresh mocks per test" requirement.
        /// </summary>
        /// <param name="userManager">The mocked <see cref="UserManager{AppUser}"/>.</param>
        /// <param name="signInManager">The mocked <see cref="SignInManager{AppUser}"/> (wrapping <paramref name="userManager"/>).</param>
        /// <param name="tokenService">The mocked <see cref="ITokenService"/>.</param>
        /// <param name="mapper">The mocked AutoMapper <see cref="IMapper"/>.</param>
        /// <returns>A ready-to-exercise <see cref="AccountController"/>.</returns>
        private static AccountController CreateController(
            out Mock<UserManager<AppUser>> userManager,
            out Mock<SignInManager<AppUser>> signInManager,
            out Mock<ITokenService> tokenService,
            out Mock<IMapper> mapper)
        {
            userManager = ControllerTestHelpers.GetMockUserManager();
            signInManager = ControllerTestHelpers.GetMockSignInManager(userManager);
            tokenService = new Mock<ITokenService>();
            mapper = new Mock<IMapper>();

            return new AccountController(
                userManager.Object, signInManager.Object, tokenService.Object, mapper.Object);
        }

        // ==================================================================
        // CheckEmailExistsAsync — GET api/account/emailexists (direct bool)
        // ==================================================================

        [Fact]
        public async Task CheckEmailExistsAsync_WhenEmailInUse_ReturnsTrue()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out _, out _);
            userManager.Setup(m => m.FindByEmailAsync("used@test.com"))
                .ReturnsAsync(new AppUser());

            // Act
            var result = await controller.CheckEmailExistsAsync("used@test.com");

            // Assert
            result.Value.Should().BeTrue();
        }

        [Fact]
        public async Task CheckEmailExistsAsync_WhenEmailNotInUse_ReturnsFalse()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out _, out _);
            userManager.Setup(m => m.FindByEmailAsync("free@test.com"))
                .ReturnsAsync((AppUser)null);

            // Act
            var result = await controller.CheckEmailExistsAsync("free@test.com");

            // Assert
            result.Value.Should().BeFalse();
        }

        // ==================================================================
        // Login — POST api/account/login (Unauthorized wrapper / direct UserDto)
        // ==================================================================

        [Fact]
        public async Task Login_WhenUserNotFound_ReturnsUnauthorized()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out _, out _);
            var loginDto = new LoginDto { Email = "missing@test.com", Password = "Pa$$w0rd" };
            userManager.Setup(m => m.FindByEmailAsync(loginDto.Email))
                .ReturnsAsync((AppUser)null);

            // Act
            var result = await controller.Login(loginDto);

            // Assert
            result.Result.Should().BeOfType<UnauthorizedObjectResult>();
            var unauthorized = (UnauthorizedObjectResult)result.Result;
            unauthorized.Value.Should().BeOfType<ApiResponse>();
            ((ApiResponse)unauthorized.Value).StatusCode.Should().Be(401);
        }

        [Fact]
        public async Task Login_WhenPasswordInvalid_ReturnsUnauthorized()
        {
            // Arrange
            var controller = CreateController(out var userManager, out var signInManager, out _, out _);
            var loginDto = new LoginDto { Email = "bob@test.com", Password = "wrong" };
            // Hold the exact instance FindByEmailAsync resolves so we can prove the controller
            // forwards *that* user (not some other/any user) into the credential check.
            var resolvedUser = new AppUser { Email = loginDto.Email };
            userManager.Setup(m => m.FindByEmailAsync(loginDto.Email))
                .ReturnsAsync(resolvedUser);
            // Match ONLY the exact resolved user + the exact DTO password (persistent=false). A
            // regression that checked a different user or a different password would not match.
            signInManager.Setup(s => s.CheckPasswordSignInAsync(resolvedUser, loginDto.Password, false))
                .ReturnsAsync(Microsoft.AspNetCore.Identity.SignInResult.Failed);

            // Act
            var result = await controller.Login(loginDto);

            // Assert
            result.Result.Should().BeOfType<UnauthorizedObjectResult>();
            var unauthorized = (UnauthorizedObjectResult)result.Result;
            unauthorized.Value.Should().BeOfType<ApiResponse>();
            ((ApiResponse)unauthorized.Value).StatusCode.Should().Be(401);
            // Prove exact credential propagation: the resolved user and the DTO password reached
            // CheckPasswordSignInAsync exactly once with persistent=false.
            signInManager.Verify(
                s => s.CheckPasswordSignInAsync(resolvedUser, loginDto.Password, false), Times.Once);
        }

        [Fact]
        public async Task Login_WithValidCredentials_ReturnsUserDtoWithToken()
        {
            // Arrange
            var controller = CreateController(out var userManager, out var signInManager, out var tokenService, out _);
            var loginDto = new LoginDto { Email = "bob@test.com", Password = "Pa$$w0rd" };
            // Hold the exact resolved user so both the credential check and the token issuance
            // can be proven to operate on *this* user.
            var resolvedUser = new AppUser { Email = "bob@test.com", DisplayName = "Bob" };
            userManager.Setup(m => m.FindByEmailAsync(loginDto.Email))
                .ReturnsAsync(resolvedUser);
            // Exact user + exact password (persistent=false) — a wrong-credential regression fails to match.
            signInManager.Setup(s => s.CheckPasswordSignInAsync(resolvedUser, loginDto.Password, false))
                .ReturnsAsync(Microsoft.AspNetCore.Identity.SignInResult.Success);
            // Mint the token ONLY for the exact resolved user; a token minted for any other user
            // would not match this setup and the token assertion below would observe the default (null).
            tokenService.Setup(t => t.CreateToken(resolvedUser)).Returns("test-token");

            // Act
            var result = await controller.Login(loginDto);

            // Assert
            result.Value.Should().NotBeNull();
            result.Value.Email.Should().Be("bob@test.com");
            result.Value.DisplayName.Should().Be("Bob");
            result.Value.Token.Should().Be("test-token");
            // Prove exact credential + token-subject propagation: the resolved user + DTO password
            // reached CheckPasswordSignInAsync once, and the token was minted for that same user once.
            signInManager.Verify(
                s => s.CheckPasswordSignInAsync(resolvedUser, loginDto.Password, false), Times.Once);
            tokenService.Verify(t => t.CreateToken(resolvedUser), Times.Once);
        }

        // ==================================================================
        // Register — POST api/account/register (validation / bad-request / direct UserDto)
        // ==================================================================

        [Fact]
        public async Task Register_WhenEmailAlreadyInUse_ReturnsBadRequestValidationError()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out _, out _);
            var dto = new RegisterDto { Email = "used@test.com", DisplayName = "X", Password = "Pa$$w0rd" };
            // Register calls its own CheckEmailExistsAsync(...).Result.Value, driven purely by FindByEmailAsync.
            userManager.Setup(m => m.FindByEmailAsync(dto.Email))
                .ReturnsAsync(new AppUser());

            // Act
            var result = await controller.Register(dto);

            // Assert
            result.Result.Should().BeOfType<BadRequestObjectResult>();
            var badRequest = (BadRequestObjectResult)result.Result;
            badRequest.Value.Should().BeOfType<ApiValidationErrorResponose>();
            var validationError = (ApiValidationErrorResponose)badRequest.Value;
            validationError.StatusCode.Should().Be(400);
            validationError.Errors.Should().Contain("Email address already in use");
        }

        [Fact]
        public async Task Register_WhenCreateFails_ReturnsBadRequestApiResponse400()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out _, out _);
            var dto = new RegisterDto { Email = "new@test.com", DisplayName = "New", Password = "Pa$$w0rd" };
            userManager.Setup(m => m.FindByEmailAsync(dto.Email))
                .ReturnsAsync((AppUser)null);
            // Capture the AppUser the controller constructs internally so we can prove it is built
            // from the exact registration DTO fields (not defaulted or mis-mapped).
            AppUser createdUser = null;
            userManager.Setup(m => m.CreateAsync(It.IsAny<AppUser>(), dto.Password))
                .Callback<AppUser, string>((u, p) => createdUser = u)
                .ReturnsAsync(IdentityResult.Failed(new IdentityError { Code = "WeakPassword", Description = "weak" }));

            // Act
            var result = await controller.Register(dto);

            // Assert
            result.Result.Should().BeOfType<BadRequestObjectResult>();
            var badRequest = (BadRequestObjectResult)result.Result;
            badRequest.Value.Should().BeOfType<ApiResponse>();
            ((ApiResponse)badRequest.Value).StatusCode.Should().Be(400);
            // Prove exact account-creation data flow: email, display name, and username (== email)
            // are taken from the DTO, and the DTO password is the credential passed to CreateAsync once.
            createdUser.Should().NotBeNull();
            createdUser.Email.Should().Be(dto.Email);
            createdUser.DisplayName.Should().Be(dto.DisplayName);
            createdUser.UserName.Should().Be(dto.Email);
            userManager.Verify(
                m => m.CreateAsync(
                    It.Is<AppUser>(u => u.Email == dto.Email
                                        && u.DisplayName == dto.DisplayName
                                        && u.UserName == dto.Email),
                    dto.Password),
                Times.Once);
        }

        [Fact]
        public async Task Register_WithValidData_ReturnsUserDtoWithToken()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out var tokenService, out _);
            var dto = new RegisterDto { Email = "new@test.com", DisplayName = "New", Password = "Pa$$w0rd" };
            userManager.Setup(m => m.FindByEmailAsync(dto.Email))
                .ReturnsAsync((AppUser)null);
            // Capture the internally-constructed user so we can prove the token is minted for the
            // exact user that was just created (same instance), not for any/other user.
            AppUser createdUser = null;
            userManager.Setup(m => m.CreateAsync(It.IsAny<AppUser>(), dto.Password))
                .Callback<AppUser, string>((u, p) => createdUser = u)
                .ReturnsAsync(IdentityResult.Success);
            tokenService.Setup(t => t.CreateToken(It.IsAny<AppUser>())).Returns("test-token");

            // Act
            var result = await controller.Register(dto);

            // Assert
            result.Value.Should().NotBeNull();
            result.Value.Email.Should().Be(dto.Email);
            result.Value.DisplayName.Should().Be(dto.DisplayName);
            result.Value.Token.Should().Be("test-token");
            // Prove exact account-creation data flow and token subject: the created user carries the
            // exact DTO fields, CreateAsync received it with the DTO password once, and the token was
            // minted for that exact created user once.
            createdUser.Should().NotBeNull();
            createdUser.Email.Should().Be(dto.Email);
            createdUser.DisplayName.Should().Be(dto.DisplayName);
            createdUser.UserName.Should().Be(dto.Email);
            userManager.Verify(
                m => m.CreateAsync(
                    It.Is<AppUser>(u => u.Email == dto.Email
                                        && u.DisplayName == dto.DisplayName
                                        && u.UserName == dto.Email),
                    dto.Password),
                Times.Once);
            tokenService.Verify(t => t.CreateToken(createdUser), Times.Once);
        }

        // ==================================================================
        // GetCurrentUser — [Authorize] GET api/account (async Users via static extension)
        // ==================================================================

        [Fact]
        public async Task GetCurrentUser_WithAuthenticatedUser_ReturnsUserDto()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out var tokenService, out _);
            // Seed MORE THAN ONE user so the email predicate is actually exercised. The production
            // extension runs Users.SingleOrDefaultAsync(x => x.Email == email): with two distinct
            // emails a missing/incorrect predicate would either throw (>1 match) or select the wrong
            // user, so a green result proves the authenticated email scopes to exactly one user.
            var bob = new AppUser { Email = "bob@test.com", DisplayName = "Bob" };
            var alice = new AppUser { Email = "alice@test.com", DisplayName = "Alice" };
            ControllerTestHelpers.MockUserManagerUsers(userManager, new List<AppUser> { alice, bob });
            // Mint a token ONLY for Bob so the token subject is provably the authenticated user.
            tokenService.Setup(t => t.CreateToken(bob)).Returns("bob-token");
            controller.WithUser(ControllerTestHelpers.GetClaimsPrincipal("bob@test.com"));

            // Act
            var result = await controller.GetCurrentUser();

            // Assert
            result.Value.Should().NotBeNull();
            result.Value.Email.Should().Be("bob@test.com");
            result.Value.DisplayName.Should().Be("Bob");
            result.Value.Token.Should().Be("bob-token");
            // The token must be issued for Bob (the authenticated user) and never for Alice.
            tokenService.Verify(t => t.CreateToken(bob), Times.Once);
            tokenService.Verify(t => t.CreateToken(alice), Times.Never);
        }

        // ==================================================================
        // GetUserAddress — [Authorize] GET api/account/address (async Users; Include is a no-op here)
        // ==================================================================

        [Fact]
        public async Task GetUserAddress_WithAuthenticatedUser_ReturnsMappedAddressDto()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out _, out var mapper);
            // Seed two users, each with a distinct address, so correct email scoping is provable:
            // FindUserByClaimsPrincipleWithAddressAsync must select Bob and map Bob's address.
            var bobAddress = new Address { FirstName = "Bob", City = "Gotham" };
            var aliceAddress = new Address { FirstName = "Alice", City = "Metropolis" };
            var bob = new AppUser { Email = "bob@test.com", Address = bobAddress };
            var alice = new AppUser { Email = "alice@test.com", Address = aliceAddress };
            ControllerTestHelpers.MockUserManagerUsers(userManager, new List<AppUser> { alice, bob });
            var bobDto = new AddressDto { FirstName = "Bob", City = "Gotham" };
            // Only Bob's address maps to a known DTO; selecting the wrong user would map an
            // unconfigured address and fail the reference-equality assertion below.
            mapper.Setup(m => m.Map<Address, AddressDto>(bobAddress)).Returns(bobDto);
            controller.WithUser(ControllerTestHelpers.GetClaimsPrincipal("bob@test.com"));

            // Act
            var result = await controller.GetUserAddress();

            // Assert
            result.Value.Should().BeSameAs(bobDto);
            // The authenticated user's address (Bob's) is the one mapped — never Alice's.
            mapper.Verify(m => m.Map<Address, AddressDto>(bobAddress), Times.Once);
            mapper.Verify(m => m.Map<Address, AddressDto>(aliceAddress), Times.Never);
        }

        // ==================================================================
        // UpdateUserAddress — [Authorize] PUT api/account/address (Ok / BadRequest string)
        // ==================================================================

        [Fact]
        public async Task UpdateUserAddress_WhenUpdateSucceeds_ReturnsOkWithAddressDto()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out _, out var mapper);
            // Two seeded users prove the authenticated email selects Bob for the update (not Alice).
            var bob = new AppUser { Email = "bob@test.com", Address = new Address { FirstName = "OldBob" } };
            var alice = new AppUser { Email = "alice@test.com", Address = new Address { FirstName = "Alice" } };
            ControllerTestHelpers.MockUserManagerUsers(userManager, new List<AppUser> { alice, bob });
            // A fully-populated DTO and its mapped Address let us assert every field is persisted.
            var inputDto = new AddressDto
            {
                Id = 5, FirstName = "Bob", LastName = "Smith", Street = "1 Main St",
                City = "Gotham", State = "NY", ZipCode = "10001"
            };
            var mappedAddress = new Address
            {
                Id = 5, FirstName = "Bob", LastName = "Smith", Street = "1 Main St",
                City = "Gotham", State = "NY", ZipCode = "10001"
            };
            mapper.Setup(m => m.Map<AddressDto, Address>(inputDto)).Returns(mappedAddress);
            var returnDto = new AddressDto
            {
                Id = 5, FirstName = "Bob", LastName = "Smith", Street = "1 Main St",
                City = "Gotham", State = "NY", ZipCode = "10001"
            };
            // Map back ONLY the mutated address instance — proves the persisted address is echoed.
            mapper.Setup(m => m.Map<Address, AddressDto>(mappedAddress)).Returns(returnDto);
            // Capture the exact user handed to UpdateAsync so we can assert scoping + mutation.
            AppUser updatedUser = null;
            userManager.Setup(m => m.UpdateAsync(It.IsAny<AppUser>()))
                .Callback<AppUser>(u => updatedUser = u)
                .ReturnsAsync(IdentityResult.Success);
            controller.WithUser(ControllerTestHelpers.GetClaimsPrincipal("bob@test.com"));

            // Act
            var result = await controller.UpdateUserAddress(inputDto);

            // Assert
            result.Result.Should().BeOfType<OkObjectResult>();
            var ok = (OkObjectResult)result.Result;
            ok.Value.Should().BeSameAs(returnDto);
            // Correct user selected by the authenticated email.
            updatedUser.Should().BeSameAs(bob);
            updatedUser.Email.Should().Be("bob@test.com");
            // The address was mutated to the mapped Address (all seven fields) before persistence.
            updatedUser.Address.Should().BeSameAs(mappedAddress);
            updatedUser.Address.Id.Should().Be(5);
            updatedUser.Address.FirstName.Should().Be("Bob");
            updatedUser.Address.LastName.Should().Be("Smith");
            updatedUser.Address.Street.Should().Be("1 Main St");
            updatedUser.Address.City.Should().Be("Gotham");
            updatedUser.Address.State.Should().Be("NY");
            updatedUser.Address.ZipCode.Should().Be("10001");
            // Exactly one persistence call, made for the correct user, with both mappings used once.
            userManager.Verify(m => m.UpdateAsync(bob), Times.Once);
            mapper.Verify(m => m.Map<AddressDto, Address>(inputDto), Times.Once);
            mapper.Verify(m => m.Map<Address, AddressDto>(mappedAddress), Times.Once);
        }

        [Fact]
        public async Task UpdateUserAddress_WhenUpdateFails_ReturnsBadRequestWithMessage()
        {
            // Arrange
            var controller = CreateController(out var userManager, out _, out _, out var mapper);
            // Two seeded users prove the failed-update path also scopes to the authenticated user.
            var bob = new AppUser { Email = "bob@test.com", Address = new Address { FirstName = "OldBob" } };
            var alice = new AppUser { Email = "alice@test.com", Address = new Address { FirstName = "Alice" } };
            ControllerTestHelpers.MockUserManagerUsers(userManager, new List<AppUser> { alice, bob });
            var inputDto = new AddressDto
            {
                Id = 7, FirstName = "Bob", LastName = "Smith", Street = "1 Main St",
                City = "Gotham", State = "NY", ZipCode = "10001"
            };
            var mappedAddress = new Address
            {
                Id = 7, FirstName = "Bob", LastName = "Smith", Street = "1 Main St",
                City = "Gotham", State = "NY", ZipCode = "10001"
            };
            mapper.Setup(m => m.Map<AddressDto, Address>(inputDto)).Returns(mappedAddress);
            // Capture the persisted user to prove scoping even on the failure branch.
            AppUser updatedUser = null;
            userManager.Setup(m => m.UpdateAsync(It.IsAny<AppUser>()))
                .Callback<AppUser>(u => updatedUser = u)
                .ReturnsAsync(IdentityResult.Failed(new IdentityError { Code = "UpdateFailed", Description = "nope" }));
            controller.WithUser(ControllerTestHelpers.GetClaimsPrincipal("bob@test.com"));

            // Act
            var result = await controller.UpdateUserAddress(inputDto);

            // Assert
            result.Result.Should().BeOfType<BadRequestObjectResult>();
            var badRequest = (BadRequestObjectResult)result.Result;
            // NOTE: exact production string, "then" typo included — do NOT "correct" it.
            badRequest.Value.Should().Be("Problem updating then user");
            // The failed update was attempted exactly once, for the correct (authenticated) user,
            // whose address had already been mutated to the mapped Address before persistence.
            updatedUser.Should().BeSameAs(bob);
            updatedUser.Address.Should().BeSameAs(mappedAddress);
            userManager.Verify(m => m.UpdateAsync(bob), Times.Once);
        }
    }
}
