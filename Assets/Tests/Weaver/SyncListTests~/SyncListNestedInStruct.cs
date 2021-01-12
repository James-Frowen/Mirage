using Mirror;

namespace SyncListTests.SyncListNestedInStruct
{
    class SyncListNestedStruct : NetworkBehaviour
    {
        SomeData.SyncList Foo;


        public struct SomeData
        {
            public int usefulNumber;

            public class SyncList : Mirror.SyncList<SomeData> { }
        }
    }
}