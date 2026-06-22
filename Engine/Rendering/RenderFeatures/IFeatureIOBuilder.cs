namespace BallisticEngine;

public interface IFeatureIOBuilder {
    void Read(string handleName);

    void Write(string handleName);

    void ReadWrite(string handleName);

    string RequestScratch(string roleName);

    void AllowCulling(bool allow = true);
}
