namespace livekitmeet.Services;

public sealed record PushDeviceRegistrationRequest(string Token, string Platform = "android");
