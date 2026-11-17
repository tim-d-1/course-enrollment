using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Courses.Tests;

[Collection("Database")]
public class CoursesApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient client;

    public CoursesApiTests(WebApplicationFactory<Program> factory)
    {
        client = factory.CreateClient();
        using var connection = Database.Connect();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "TRUNCATE enrollments, courses, students RESTART IDENTITY CASCADE";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        client.Dispose();
    }

    private record StudentInput(string Code, int Age, int EntryScore, bool Regular = false);
    private record CourseInput(string Code, long Price, int Capacity, int Level = 1);
    private record EnrollmentInput(string Reference, long StudentId, long CourseId);
    private record ErrorResponse(string Code, string Message);

    [Fact]
    public async Task CreateStudent_Returns201AndLocation()
    {
        var response = await client.PostAsJsonAsync("/students", new StudentInput("ST-API-1", 20, 85, false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var student = await response.Content.ReadFromJsonAsync<Student>(jsonOptions);
        Assert.NotNull(student);
        Assert.True(student.Id > 0);
        Assert.Equal("ST-API-1", student.Code);
        Assert.Equal($"/students/{student.Id}", response.Headers.Location.ToString());
    }

    [Fact]
    public async Task GetStudentById_Returns200AndData()
    {
        var postResponse = await client.PostAsJsonAsync("/students", new StudentInput("ST-API-2", 22, 90, true));
        var created = await postResponse.Content.ReadFromJsonAsync<Student>(jsonOptions);

        var getResponse = await client.GetAsync($"/students/{created!.Id}");

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var student = await getResponse.Content.ReadFromJsonAsync<Student>(jsonOptions);
        Assert.NotNull(student);
        Assert.Equal(created.Id, student.Id);
        Assert.Equal("ST-API-2", student.Code);
        Assert.Equal(22, student.Age);
        Assert.Equal(90, student.EntryScore);
        Assert.True(student.Regular);
    }

    [Fact]
    public async Task GetNonExistentResource_Returns404WithErrorContract()
    {
        var response = await client.GetAsync("/enrollments/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(jsonOptions);
        Assert.NotNull(error);
        Assert.Equal("NOT_FOUND", error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task CompleteEnrollment_Returns200AndNewState()
    {
        var studentRes = await client.PostAsJsonAsync("/students", new StudentInput("ST-API-3", 21, 80, false));
        var student = await studentRes.Content.ReadFromJsonAsync<Student>(jsonOptions);
        var courseRes = await client.PostAsJsonAsync("/courses", new CourseInput("CR-API-3", 400000, 10, 1));
        var course = await courseRes.Content.ReadFromJsonAsync<Course>(jsonOptions);
        var enrollRes = await client.PostAsJsonAsync("/enrollments", new EnrollmentInput("REF-API-3", student!.Id, course!.Id));
        var enrollment = await enrollRes.Content.ReadFromJsonAsync<Enrollment>(jsonOptions);

        var completeRes = await client.PostAsync($"/enrollments/{enrollment!.Id}/complete", null);

        Assert.Equal(HttpStatusCode.OK, completeRes.StatusCode);
        var completed = await completeRes.Content.ReadFromJsonAsync<Enrollment>(jsonOptions);
        Assert.NotNull(completed);
        Assert.Equal(EnrollmentStatus.Completed, completed.Status);
        Assert.Equal(0, completed.Refund);
        Assert.Equal(400000, completed.Total);
    }

    [Fact]
    public async Task CancelEnrollment_Returns200AndRefund()
    {
        var studentRes = await client.PostAsJsonAsync("/students", new StudentInput("ST-API-4", 21, 80, false));
        var student = await studentRes.Content.ReadFromJsonAsync<Student>(jsonOptions);
        var courseRes = await client.PostAsJsonAsync("/courses", new CourseInput("CR-API-4", 400000, 10, 1));
        var course = await courseRes.Content.ReadFromJsonAsync<Course>(jsonOptions);
        var enrollRes = await client.PostAsJsonAsync("/enrollments", new EnrollmentInput("REF-API-4", student!.Id, course!.Id));
        var enrollment = await enrollRes.Content.ReadFromJsonAsync<Enrollment>(jsonOptions);

        var cancelRes = await client.PostAsync($"/enrollments/{enrollment!.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, cancelRes.StatusCode);
        var cancelled = await cancelRes.Content.ReadFromJsonAsync<Enrollment>(jsonOptions);
        Assert.NotNull(cancelled);
        Assert.Equal(EnrollmentStatus.Cancelled, cancelled.Status);
        Assert.Equal(100000, cancelled.Refund);
        Assert.Equal(100000, cancelled.Prepaid);
    }

    [Fact]
    public async Task ForbiddenAction_RepeatedComplete_Returns409AndErrorContract()
    {
        var studentRes = await client.PostAsJsonAsync("/students", new StudentInput("ST-API-5", 21, 80, false));
        var student = await studentRes.Content.ReadFromJsonAsync<Student>(jsonOptions);
        var courseRes = await client.PostAsJsonAsync("/courses", new CourseInput("CR-API-5", 300000, 10, 1));
        var course = await courseRes.Content.ReadFromJsonAsync<Course>(jsonOptions);
        var enrollRes = await client.PostAsJsonAsync("/enrollments", new EnrollmentInput("REF-API-5", student!.Id, course!.Id));
        var enrollment = await enrollRes.Content.ReadFromJsonAsync<Enrollment>(jsonOptions);

        await client.PostAsync($"/enrollments/{enrollment!.Id}/complete", null);
        var repeatedRes = await client.PostAsync($"/enrollments/{enrollment.Id}/complete", null);

        Assert.Equal(HttpStatusCode.Conflict, repeatedRes.StatusCode);
        var error = await repeatedRes.Content.ReadFromJsonAsync<ErrorResponse>(jsonOptions);
        Assert.NotNull(error);
        Assert.Equal("ENROLLMENT_CONFLICT", error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task ImmutabilityAfterRejection_StatusUnchangedAfter409()
    {
        var studentRes = await client.PostAsJsonAsync("/students", new StudentInput("ST-API-6", 21, 80, false));
        var student = await studentRes.Content.ReadFromJsonAsync<Student>(jsonOptions);
        var courseRes = await client.PostAsJsonAsync("/courses", new CourseInput("CR-API-6", 300000, 10, 1));
        var course = await courseRes.Content.ReadFromJsonAsync<Course>(jsonOptions);
        var enrollRes = await client.PostAsJsonAsync("/enrollments", new EnrollmentInput("REF-API-6", student!.Id, course!.Id));
        var enrollment = await enrollRes.Content.ReadFromJsonAsync<Enrollment>(jsonOptions);

        await client.PostAsync($"/enrollments/{enrollment!.Id}/complete", null);
        var conflictRes = await client.PostAsync($"/enrollments/{enrollment.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, conflictRes.StatusCode);

        var getRes = await client.GetAsync($"/enrollments/{enrollment.Id}");
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);
        var current = await getRes.Content.ReadFromJsonAsync<Enrollment>(jsonOptions);
        Assert.NotNull(current);
        Assert.Equal(EnrollmentStatus.Completed, current.Status);
        Assert.Equal(0, current.Refund);
        Assert.Equal(300000, current.Total);
    }

    [Theory]
    [InlineData(800000, 200000)]
    [InlineData(100000, 50000)]
    [InlineData(30000, 30000)]
    public async Task PersonalPrepaymentRule_CalculatesExpectedPrepaid(long price, long expectedPrepaid)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var studentRes = await client.PostAsJsonAsync("/students", new StudentInput($"ST-PR-{suffix}", 22, 85, false));
        var student = await studentRes.Content.ReadFromJsonAsync<Student>(jsonOptions);
        var courseRes = await client.PostAsJsonAsync("/courses", new CourseInput($"CR-PR-{suffix}", price, 10, 1));
        var course = await courseRes.Content.ReadFromJsonAsync<Course>(jsonOptions);

        var enrollRes = await client.PostAsJsonAsync("/enrollments", new EnrollmentInput($"REF-PR-{suffix}", student!.Id, course!.Id));

        Assert.Equal(HttpStatusCode.Created, enrollRes.StatusCode);
        var enrollment = await enrollRes.Content.ReadFromJsonAsync<Enrollment>(jsonOptions);
        Assert.NotNull(enrollment);
        Assert.Equal(price, enrollment.Total);
        Assert.Equal(expectedPrepaid, enrollment.Prepaid);

        var getRes = await client.GetAsync($"/enrollments/{enrollment.Id}");
        var stored = await getRes.Content.ReadFromJsonAsync<Enrollment>(jsonOptions);
        Assert.NotNull(stored);
        Assert.Equal(expectedPrepaid, stored.Prepaid);
    }

    [Fact]
    public async Task Workflow_PostGetActionGet_PassesDynamicId()
    {
        var studentRes = await client.PostAsJsonAsync("/students", new StudentInput("ST-WF-1", 20, 80, false));
        var student = await studentRes.Content.ReadFromJsonAsync<Student>(jsonOptions);
        var courseRes = await client.PostAsJsonAsync("/courses", new CourseInput("CR-WF-1", 500000, 10, 1));
        var course = await courseRes.Content.ReadFromJsonAsync<Course>(jsonOptions);

        var postRes = await client.PostAsJsonAsync("/enrollments", new EnrollmentInput("REF-WF-1", student!.Id, course!.Id));
        Assert.Equal(HttpStatusCode.Created, postRes.StatusCode);
        var created = await postRes.Content.ReadFromJsonAsync<Enrollment>(jsonOptions);
        Assert.NotNull(created);
        Assert.True(created.Id > 0);

        var getAfterCreate = await client.GetAsync($"/enrollments/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getAfterCreate.StatusCode);
        var readCreated = await getAfterCreate.Content.ReadFromJsonAsync<Enrollment>(jsonOptions);
        Assert.NotNull(readCreated);
        Assert.Equal(EnrollmentStatus.Enrolled, readCreated.Status);

        var actionRes = await client.PostAsync($"/enrollments/{created.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, actionRes.StatusCode);

        var getAfterAction = await client.GetAsync($"/enrollments/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getAfterAction.StatusCode);
        var finalState = await getAfterAction.Content.ReadFromJsonAsync<Enrollment>(jsonOptions);
        Assert.NotNull(finalState);
        Assert.Equal(EnrollmentStatus.Cancelled, finalState.Status);
        Assert.Equal(created.Prepaid, finalState.Refund);
    }

    [Fact]
    public async Task DuplicateEnrollment_Returns409Conflict()
    {
        var studentRes = await client.PostAsJsonAsync("/students", new StudentInput("ST-DUP-1", 20, 80, false));
        var student = await studentRes.Content.ReadFromJsonAsync<Student>(jsonOptions);
        var courseRes = await client.PostAsJsonAsync("/courses", new CourseInput("CR-DUP-1", 200000, 10, 1));
        var course = await courseRes.Content.ReadFromJsonAsync<Course>(jsonOptions);

        var firstRes = await client.PostAsJsonAsync("/enrollments", new EnrollmentInput("REF-DUP-1", student!.Id, course!.Id));
        Assert.Equal(HttpStatusCode.Created, firstRes.StatusCode);

        var duplicateRes = await client.PostAsJsonAsync("/enrollments", new EnrollmentInput("REF-DUP-2", student.Id, course.Id));
        Assert.Equal(HttpStatusCode.Conflict, duplicateRes.StatusCode);
        var error = await duplicateRes.Content.ReadFromJsonAsync<ErrorResponse>(jsonOptions);
        Assert.NotNull(error);
        Assert.Equal("ENROLLMENT_CONFLICT", error.Code);
    }

    [Fact]
    public async Task InvalidRequest_Returns400BadRequest()
    {
        var response = await client.PostAsJsonAsync("/courses", new CourseInput("", 50, 0, 0));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(jsonOptions);
        Assert.NotNull(error);
        Assert.Equal("INVALID_REQUEST", error.Code);
    }
}
