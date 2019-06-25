using System.IO;
using System.Text;
using CompressionLzma;
using Xunit;

namespace UnitTests
{

    public class CompresstionTests
    {
        [Fact]
        public void TestMe()
        {
            var h = new CompressionHelper();
            string li =
                @"Lorem ipsum dolor sit amet, consectetur adipiscing elit.In justo lectus, laoreet sit amet lectus feugiat, iaculis porta risus.Mauris egestas lectus sit amet tincidunt sollicitudin.Fusce aliquet aliquam dictum. Etiam dapibus metus in cursus tristique. Maecenas rhoncus sollicitudin porta. Aliquam congue augue tellus, ac hendrerit risus rhoncus accumsan. Suspendisse potenti. Vivamus nibh ipsum, ornare et ipsum ut, ullamcorper condimentum dolor. Maecenas ac risus augue. Vivamus fringilla, sem vel venenatis volutpat, augue urna facilisis eros, ut laoreet enim nibh quis ante.Proin nec felis efficitur, venenatis purus eget, ultrices metus.

Fusce tristique, arcu in malesuada lobortis, nunc ante aliquam purus, nec pulvinar augue sem vehicula purus. Praesent pulvinar lacus nec quam eleifend, at aliquet arcu egestas.Curabitur in libero porttitor, volutpat augue vitae, venenatis velit.Pellentesque sagittis mattis odio. Praesent vel iaculis ex, at vulputate lorem. Donec tempus vehicula vulputate. Vestibulum ante ipsum primis in faucibus orci luctus et ultrices posuere cubilia Curae; Pellentesque ornare velit vitae mauris tristique placerat.Maecenas rutrum aliquam dolor, eu laoreet sem sagittis eget. Nunc nec tortor condimentum, ultrices lorem sit amet, scelerisque ligula. Aenean vel massa justo. Aliquam gravida odio non nunc condimentum lacinia.Aliquam molestie consectetur turpis, at dignissim sapien rutrum ut.

In nec tempor risus. Quisque maximus, leo ut fringilla viverra, enim lorem vulputate diam, at sollicitudin neque metus nec velit.Phasellus efficitur augue at est congue, eget pretium nisi facilisis.Ut dapibus ligula quis risus suscipit, in fermentum urna bibendum.Duis aliquam lobortis massa, a dictum massa lobortis sed. Suspendisse placerat mollis feugiat. Aenean ut elementum urna, id aliquet est. Ut sagittis nec est vel sollicitudin. Ut pulvinar lacus in felis consequat rhoncus.Mauris elit leo, dignissim sit amet ipsum id, rhoncus faucibus leo.Duis ac orci luctus, facilisis purus nec, porta sem.Duis aliquet purus leo, aliquam varius purus posuere vitae. Aenean vestibulum rhoncus neque, eu facilisis eros maximus at. Integer laoreet lacinia magna, eu egestas sapien volutpat in. Maecenas dictum mattis dui, sit amet elementum diam dignissim eget.Curabitur consectetur dignissim diam, a sodales leo venenatis nec.

Aliquam semper eget turpis sit amet condimentum.Ut vel imperdiet arcu. Aliquam a tellus maximus, blandit felis ut, blandit leo.Aenean nisl odio, tempus eu aliquet eget, accumsan et metus. Curabitur at nunc at nunc pretium suscipit in sed nisl. In tincidunt lacus sed diam eleifend vestibulum.Proin sed nisi in diam tempus scelerisque eget eget massa. Curabitur maximus mauris ac lorem viverra porta.Suspendisse mattis purus nec arcu sollicitudin, vitae elementum justo finibus.

Pellentesque a suscipit nisl, ut laoreet erat. Duis eu volutpat est, non auctor sapien. Duis feugiat et libero non molestie. Maecenas dictum felis fringilla mauris facilisis dictum.Sed vehicula sit amet lorem vitae interdum.Ut vitae tortor ligula. Aliquam in rutrum velit. In rhoncus ex nunc, nec vestibulum nisi volutpat eu. Vivamus ac odio non purus mattis placerat sit amet non enim.Donec pretium eros a facilisis dignissim. ";

            var loremBytes = Encoding.UTF8.GetBytes(li);

            //1352 bytes med magi.
            var squeezedLoremBytes = h.Compress(loremBytes);
            var roundtrippedLoremBytes = h.Decompress(squeezedLoremBytes);
            var loremAgain = Encoding.UTF8.GetString(roundtrippedLoremBytes);
        }
    }
}
