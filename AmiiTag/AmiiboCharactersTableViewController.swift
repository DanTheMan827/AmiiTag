//
//  AmiiboCharactersTableViewController.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 7/12/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit

class AmiiboCharactersTableViewController: UITableViewController {
    var amiiboCharacters: [TagDump] = []
    var seriesFilter: String? = nil
    
    // MARK: UIViewController
    override func viewDidLoad() {
        super.viewDidLoad()
        
        amiiboCharacters = AmiiboDatabase.amiiboDumps.values.filter({ (dump: TagDump) -> Bool in
            if seriesFilter == nil {
                return true
            }
            
            return dump.amiiboSeriesHex == seriesFilter
        }).sorted(by: { (a, b) -> Bool in
            if let aName = a.amiiboName, let bName = b.amiiboName {
                return aName < bName
            }
            return false
        })
    }
    
    // MARK: UITableViewDataSource
    override func numberOfSections(in tableView: UITableView) -> Int {
        return 1
    }
    override func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        return amiiboCharacters.count
    }
    override func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let index = (indexPath as NSIndexPath).row
        if let tableView = self.view as? UITableView {
            let cell = tableView.dequeueReusableCell(withIdentifier: "CharacterTableCell") as! AmiiboCharacterTableViewCell
            cell.CellLabel.text = amiiboCharacters[index].amiiboName
            cell.CellImage.image = amiiboCharacters[index].image
            
            return cell
        }
        
        return UITableViewCell(frame: CGRect.zero)
    }
    override func tableView(_ tableView: UITableView, heightForRowAt indexPath: IndexPath) -> CGFloat {
        return 100.0
    }
    
    // MARK: UITableViewDelegate
    override func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        (view as? UITableView)?.deselectRow(at: indexPath, animated: true)
        let randomUidSig = NTAG215Tag.uidSignatures.randomElement()!
        if let patched = try? amiiboCharacters[indexPath.row].patchedDump(withUID: randomUidSig.uid, staticKey: KeyFiles.staticKey!, dataKey: KeyFiles.dataKey!, skipDecrypt: true) {
            MainViewController.openTagInfo(dump: TagDump(data: Data(patched.data[0..<532] + Data(count: 8) + randomUidSig.signature))!, controller: self)
        }
    }
}
